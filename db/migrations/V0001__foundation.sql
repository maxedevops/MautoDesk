--------------------------------------------------------------------------------
-- MautoDesk — V0001 Foundation
--
-- Extensions, schemas, roles, tenant-context helpers, the append-only audit
-- ledger, the transactional outbox, and the platform/tenant/billing tables.
--
-- Conventions used by every migration in this directory:
--   * Every tenant-owned table has: id, tenant_id, created_at/by, updated_at/by,
--     deleted_at/by. Optimistic concurrency uses PostgreSQL's system column
--     `xmin` (mapped by EF Core via UseXminAsConcurrencyToken) — no explicit
--     row_version column is needed and none can drift.
--   * Soft delete is `deleted_at is not null`. It is NOT erasure; see the
--     purge path in V0005. Unique indexes are partial on `deleted_at is null`.
--   * All timestamps are `timestamptz`. The database is UTC. There is no
--     local-time column anywhere in this system.
--   * Money is `numeric(14,2)` unless a statutory rate requires more scale;
--     rates are `numeric(9,6)`. `float`/`double precision` appears nowhere.
--   * RLS is enabled and FORCED on every tenant-owned table in V0005, and a
--     test asserts that no tenant-owned table is missing a policy.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Extensions
--------------------------------------------------------------------------------
create extension if not exists pgcrypto;      -- gen_random_uuid(), digest()
create extension if not exists citext;        -- case-insensitive email/vin
create extension if not exists pg_trgm;       -- fuzzy VIN/stock/phone/name search
create extension if not exists btree_gin;     -- composite (tenant_id, tsvector) GIN
create extension if not exists unaccent;      -- search normalization

--------------------------------------------------------------------------------
-- Schemas — one per module, reinforcing the module boundaries from ADR-0001.
--------------------------------------------------------------------------------
create schema if not exists app;         -- cross-cutting infrastructure
create schema if not exists platform;    -- tenants, plans, subscriptions, platform admin
create schema if not exists identity;    -- users, roles, permissions, sessions, MFA
create schema if not exists inventory;   -- vehicles, photos, costs, recon
create schema if not exists crm;         -- customers, leads, tasks, activity
create schema if not exists sales;       -- deals, trades, jurisdictions, rule sets
create schema if not exists documents;   -- files, versions, templates, deal jackets
create schema if not exists signatures;  -- envelopes, signers, evidence packages
create schema if not exists messaging;   -- email/SMS threads and messages
create schema if not exists publishing;  -- website + marketplace syndication
create schema if not exists billing;     -- subscription metering
create schema if not exists audit;       -- the append-only ledger

comment on schema audit is
  'Append-only. The application role has INSERT and SELECT only; UPDATE and '
  'DELETE are revoked and additionally blocked by trigger.';

--------------------------------------------------------------------------------
-- Application role
--
-- The application connects as mautodesk_app. It deliberately does NOT have
-- BYPASSRLS and is NOT the table owner, so FORCE ROW LEVEL SECURITY applies to
-- it. Migrations run as a separate owner role. This separation is the entire
-- basis of ADR-0002 — do not collapse it for convenience.
--------------------------------------------------------------------------------
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'mautodesk_app') then
    create role mautodesk_app login;
  end if;
end
$$;

grant usage on schema app, platform, identity, inventory, crm, sales,
                        documents, signatures, messaging, publishing,
                        billing, audit
  to mautodesk_app;

--------------------------------------------------------------------------------
-- Tenant / user context
--
-- Set per request or per job by TenantConnectionInterceptor using
-- set_config('app.tenant_id', ..., true) — transaction-local, so a pooled
-- connection cannot leak context to the next request. Both helpers return NULL
-- when unset, which makes every RLS predicate evaluate to NULL => no rows.
-- Fail-closed is the only acceptable default here.
--------------------------------------------------------------------------------
create or replace function app.current_tenant_id() returns uuid
  language sql stable
  as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

create or replace function app.current_user_id() returns uuid
  language sql stable
  as $$ select nullif(current_setting('app.user_id', true), '')::uuid $$;

comment on function app.current_tenant_id() is
  'Returns NULL when app.tenant_id is unset, causing every RLS policy to deny. '
  'Never change this to a default or a fallback tenant.';

--------------------------------------------------------------------------------
-- Shared trigger functions
--------------------------------------------------------------------------------

-- Maintains updated_at on any table that has the column.
create or replace function app.set_updated_at() returns trigger
  language plpgsql as $$
begin
  new.updated_at := now();
  return new;
end
$$;

-- Blocks mutation of append-only tables even if a future GRANT is too generous.
create or replace function app.deny_mutation() returns trigger
  language plpgsql as $$
begin
  raise exception 'Table %.% is append-only; % is not permitted',
    tg_table_schema, tg_table_name, tg_op
    using errcode = 'insufficient_privilege';
end
$$;

-- Prevents a row from being written into, or moved to, another tenant.
-- Belt and braces alongside the RLS WITH CHECK clause.
create or replace function app.enforce_tenant() returns trigger
  language plpgsql as $$
begin
  if tg_op = 'UPDATE' and new.tenant_id is distinct from old.tenant_id then
    raise exception 'tenant_id is immutable' using errcode = 'check_violation';
  end if;
  if app.current_tenant_id() is not null
     and new.tenant_id is distinct from app.current_tenant_id() then
    raise exception 'row tenant_id does not match session tenant'
      using errcode = 'insufficient_privilege';
  end if;
  return new;
end
$$;

--------------------------------------------------------------------------------
-- platform.tenant — the dealership. This table IS the tenant boundary.
--------------------------------------------------------------------------------
create table platform.tenant (
  id                  uuid primary key default gen_random_uuid(),
  slug                citext not null,
  legal_name          text not null,
  dba_name            text,
  dealer_license_no   text,
  federal_tax_id_enc  bytea,                 -- envelope-encrypted (ADR-0007)
  federal_tax_id_kid  text,
  phone               text,
  email               citext,
  website_url         text,
  address_line1       text,
  address_line2       text,
  city                text,
  state_code          char(2),
  postal_code         text,
  country_code        char(2) not null default 'US',
  timezone            text not null default 'America/Chicago',
  status              text not null default 'trialing'
                      check (status in ('trialing','active','past_due','suspended','cancelled')),
  onboarded_at        timestamptz,
  created_at          timestamptz not null default now(),
  updated_at          timestamptz not null default now(),
  deleted_at          timestamptz
);

create unique index tenant_slug_uq on platform.tenant (slug) where deleted_at is null;
create index tenant_status_ix on platform.tenant (status) where deleted_at is null;

create trigger tenant_set_updated_at before update on platform.tenant
  for each row execute function app.set_updated_at();

-- Launch-state guard: the deal engine only has reviewed rule sets for OK/KS/TX.
-- Widen this constraint deliberately, per state, with sign-off (see V0004 §9).
alter table platform.tenant
  add constraint tenant_supported_state_ck
  check (state_code is null or state_code in ('OK','KS','TX'));

--------------------------------------------------------------------------------
-- Plans, subscriptions, usage metering
--------------------------------------------------------------------------------
create table billing.plan (
  id               uuid primary key default gen_random_uuid(),
  code             text not null unique,
  name             text not null,
  monthly_price    numeric(14,2) not null,
  annual_price     numeric(14,2),
  max_users        int,
  max_vehicles     int,
  max_ai_calls     int,          -- per month; NULL = unmetered (never used at launch)
  max_ocr_pages    int,
  max_sms          int,
  max_storage_mb   int,
  features         jsonb not null default '{}'::jsonb,
  is_public        boolean not null default true,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now()
);

create table billing.subscription (
  id                     uuid primary key default gen_random_uuid(),
  tenant_id              uuid not null references platform.tenant(id),
  plan_id                uuid not null references billing.plan(id),
  status                 text not null
                         check (status in ('trialing','active','past_due','cancelled')),
  provider               text not null default 'stripe',
  provider_customer_id   text,
  provider_subscription_id text,
  current_period_start   timestamptz,
  current_period_end     timestamptz,
  trial_ends_at          timestamptz,
  cancelled_at           timestamptz,
  created_at             timestamptz not null default now(),
  updated_at             timestamptz not null default now()
);

create unique index subscription_active_uq
  on billing.subscription (tenant_id) where status in ('trialing','active');

-- Usage is metered per tenant per period so quota enforcement (ADR-0004) is a
-- cheap read, not an aggregate over a fact table.
create table billing.usage_counter (
  tenant_id     uuid not null references platform.tenant(id),
  period        date not null,          -- first day of the billing month
  metric        text not null,          -- ai_calls | ai_input_tokens | ai_output_tokens
                                        -- | ocr_pages | sms_sent | email_sent | storage_mb
  value         bigint not null default 0,
  updated_at    timestamptz not null default now(),
  primary key (tenant_id, period, metric)
);

--------------------------------------------------------------------------------
-- audit.event — the tamper-evident, append-only ledger.
--
-- Each row stores the hash of the previous row for the same tenant, forming a
-- per-tenant chain. Altering or deleting history breaks the chain, and a
-- scheduled verifier walks each chain and alerts on a break.
--
-- This is NOT the application log. Logs are for engineers and may be sampled or
-- dropped; audit events are records and may not.
--------------------------------------------------------------------------------
create table audit.event (
  id             bigint generated always as identity primary key,
  event_id       uuid not null default gen_random_uuid(),
  tenant_id      uuid,                    -- NULL for platform-level events
  occurred_at    timestamptz not null default now(),
  actor_type     text not null
                 check (actor_type in ('user','platform_admin','system','api_key','anonymous')),
  actor_id       uuid,
  actor_display  text,
  impersonated_by uuid,                   -- platform admin acting as a tenant user
  access_reason  text,                    -- mandatory for platform_admin (checked in app)
  action         text not null,           -- e.g. 'inventory.vehicle.updated'
  entity_schema  text,
  entity_type    text,
  entity_id      uuid,
  before_state   jsonb,
  after_state    jsonb,
  metadata       jsonb not null default '{}'::jsonb,
  ip_address     inet,
  user_agent     text,
  correlation_id uuid,
  prev_hash      bytea,
  hash           bytea not null
);

create index audit_event_tenant_time_ix on audit.event (tenant_id, occurred_at desc);
create index audit_event_entity_ix      on audit.event (tenant_id, entity_type, entity_id, occurred_at desc);
create index audit_event_actor_ix       on audit.event (tenant_id, actor_id, occurred_at desc);
create index audit_event_action_ix      on audit.event (tenant_id, action, occurred_at desc);
create index audit_event_correlation_ix on audit.event (correlation_id);

-- Computes the chain hash. An advisory lock keyed on the tenant serializes
-- concurrent inserts so two rows cannot claim the same predecessor.
create or replace function audit.chain_event() returns trigger
  language plpgsql as $$
declare
  v_prev bytea;
  v_lock bigint;
begin
  v_lock := ('x' || substr(md5(coalesce(new.tenant_id::text, 'platform')), 1, 15))::bit(60)::bigint;
  perform pg_advisory_xact_lock(v_lock);

  select e.hash into v_prev
    from audit.event e
   where e.tenant_id is not distinct from new.tenant_id
   order by e.id desc
   limit 1;

  new.prev_hash := v_prev;
  new.hash := digest(
      coalesce(encode(v_prev, 'hex'), '')
      || '|' || new.event_id::text
      || '|' || coalesce(new.tenant_id::text, '')
      || '|' || extract(epoch from new.occurred_at)::text
      || '|' || new.actor_type
      || '|' || coalesce(new.actor_id::text, '')
      || '|' || new.action
      || '|' || coalesce(new.entity_type, '')
      || '|' || coalesce(new.entity_id::text, '')
      || '|' || coalesce(new.before_state::text, '')
      || '|' || coalesce(new.after_state::text, '')
      || '|' || coalesce(new.metadata::text, ''),
      'sha256');
  return new;
end
$$;

create trigger audit_event_chain before insert on audit.event
  for each row execute function audit.chain_event();

create trigger audit_event_immutable before update or delete on audit.event
  for each row execute function app.deny_mutation();

grant insert, select on audit.event to mautodesk_app;
revoke update, delete on audit.event from mautodesk_app;

--------------------------------------------------------------------------------
-- app.outbox_message — transactional outbox (ADR-0006).
--
-- Written in the SAME transaction as the state change it describes. A separate
-- dispatcher publishes and marks dispatched. This is what makes the "enter data
-- once, it flows everywhere" guarantee actually hold under failure.
--------------------------------------------------------------------------------
create table app.outbox_message (
  id             bigint generated always as identity primary key,
  message_id     uuid not null unique default gen_random_uuid(),
  tenant_id      uuid,
  event_type     text not null,
  payload        jsonb not null,
  correlation_id uuid,
  occurred_at    timestamptz not null default now(),
  available_at   timestamptz not null default now(),
  dispatched_at  timestamptz,
  attempts       int not null default 0,
  last_error     text
);

-- Partial index: the dispatcher's hot query only ever touches undispatched rows,
-- so the index stays small no matter how large the table grows.
create index outbox_pending_ix
  on app.outbox_message (available_at, id)
  where dispatched_at is null;

-- Consumer-side idempotency. Handlers are at-least-once; this makes them
-- effectively-once without requiring every handler to invent its own guard.
create table app.processed_message (
  message_id   uuid not null,
  handler      text not null,
  processed_at timestamptz not null default now(),
  primary key (message_id, handler)
);

--------------------------------------------------------------------------------
-- app.idempotency_key — API-level idempotency for mutating endpoints.
--------------------------------------------------------------------------------
create table app.idempotency_key (
  tenant_id       uuid not null,
  key             text not null,
  endpoint        text not null,
  request_hash    bytea not null,
  response_status int,
  response_body   jsonb,
  created_at      timestamptz not null default now(),
  completed_at    timestamptz,
  primary key (tenant_id, key, endpoint)
);

create index idempotency_key_created_ix on app.idempotency_key (created_at);

--------------------------------------------------------------------------------
-- app.encryption_key — wrapped data keys for envelope encryption (ADR-0007).
--
-- The master key never touches the database. Only wrapped data keys live here,
-- tagged with the master key id that wrapped them so rotation can re-wrap.
--------------------------------------------------------------------------------
create table app.encryption_key (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid,
  purpose       text not null,          -- pii | integration_credential | document
  master_kid    text not null,
  wrapped_key   bytea not null,
  algorithm     text not null default 'AES-256-GCM',
  is_active     boolean not null default true,
  created_at    timestamptz not null default now(),
  rotated_at    timestamptz
);

create index encryption_key_active_ix
  on app.encryption_key (tenant_id, purpose) where is_active;
