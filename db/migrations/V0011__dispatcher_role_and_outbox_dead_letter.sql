--------------------------------------------------------------------------------
-- MautoDesk — V0011 The dispatcher role, and outbox dead-lettering
--
-- The outbox has been written correctly since V0001 and consumed by nothing.
-- This migration gives the dispatcher what it needs to exist without weakening
-- the isolation model that makes the rest of the system defensible.
--
-- The problem it solves:
--
--   app.outbox_message carries tenant_id and is therefore under RLS, with the
--   standard policy tenant_id = app.current_tenant_id(). A dispatcher has no
--   tenant — it drains every tenant's messages — so as mautodesk_app it would
--   see exactly zero rows, forever, silently.
--
-- The two obvious fixes are both wrong:
--
--   * Granting BYPASSRLS to mautodesk_app would disable tenant isolation for
--     every request in the system to make one background loop work.
--   * A SECURITY DEFINER function returning cross-tenant payloads to
--     mautodesk_app hands a compromised web process a documented, callable way
--     to read every tenant's event stream. The two existing SECURITY DEFINER
--     functions return ids and nothing else, deliberately; this would not.
--
-- So the dispatcher gets its own login role with its own credentials and a
-- policy scoped to that role. mautodesk_app remains unable to see another
-- tenant's outbox rows, and mautodesk_dispatcher remains unable to see a
-- vehicle, a customer, or a deal. Neither can do the other's job.
--------------------------------------------------------------------------------

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'mautodesk_dispatcher') then
    -- No password here. It is set per environment, exactly as mautodesk_app's
    -- is, so a migration file never contains a credential.
    create role mautodesk_dispatcher login;
  end if;
end
$$;

comment on role mautodesk_dispatcher is
  'Drains app.outbox_message and prunes app.idempotency_key. Deliberately has '
  'no BYPASSRLS, owns nothing, and is granted nothing outside those two tables '
  'and app.processed_message. If this role is compromised, the blast radius is '
  'the event stream — not the customer database.';

grant usage on schema app to mautodesk_dispatcher;

--------------------------------------------------------------------------------
-- Dead-lettering.
--
-- Without it, a message whose handler always throws is retried until the end of
-- time, and the retry backlog eventually hides genuinely new failures. A
-- dead-lettered row stops being retried, stays visible, and keeps the error
-- that killed it.
--
-- Additive and nullable: every existing row predates the concept.
--------------------------------------------------------------------------------
alter table app.outbox_message
  add column if not exists dead_lettered_at    timestamptz,
  add column if not exists dead_letter_reason  text;

comment on column app.outbox_message.dead_lettered_at is
  'Set when the dispatcher gives up. The row is never deleted: a message that '
  'could not be delivered is exactly the thing someone will need to see.';

-- The dispatcher's hot query. V0001's outbox_pending_ix still matches every
-- undispatched row including dead-lettered ones, so it would keep serving reads
-- of rows nothing will ever pick up again.
--
-- Expand/contract: this index is added now, outbox_pending_ix is dropped in a
-- later migration once no deployed release still plans on it.
create index if not exists outbox_dispatchable_ix
  on app.outbox_message (available_at, id)
  where dispatched_at is null and dead_lettered_at is null;

-- Operators want "what is stuck" answerable without a sequential scan.
create index if not exists outbox_dead_letter_ix
  on app.outbox_message (dead_lettered_at desc)
  where dead_lettered_at is not null;

--------------------------------------------------------------------------------
-- app.idempotency_key.response_body: jsonb -> json.
--
-- A replayed response must be BYTE-identical to the original, not merely
-- equivalent to it. jsonb is a parsed representation: it discards insignificant
-- whitespace and key order, so a body stored as {"id":"..."} comes back as
-- {"id": "..."}. Semantically the same object; a different string.
--
-- That difference is invisible until it is expensive — a client comparing a
-- digest, a webhook signature computed over the body, a test asserting on an
-- exact payload. "The retry returned something equivalent" is a weaker promise
-- than the one docs/04-api-contracts.md §6 makes, and the weaker promise is not
-- the one anyone builds against.
--
-- json stores the text verbatim. Nothing queries inside this column — it is
-- written once and read back whole — so jsonb's indexing and containment
-- operators buy nothing here and cost exact reproduction.
--
-- Safe to run: the table holds at most 24 hours of keys, and nothing has
-- written to it before this release.
--------------------------------------------------------------------------------
alter table app.idempotency_key
  alter column response_body type json using response_body::text::json;

comment on column app.idempotency_key.response_body is
  'json, not jsonb, deliberately: a replay returns the original bytes. See the '
  'note in V0011.';

--------------------------------------------------------------------------------
-- Policies for the dispatcher role.
--
-- Row-level security is per-role, so this grants cross-tenant visibility to
-- mautodesk_dispatcher WITHOUT touching the policy mautodesk_app is subject to.
-- The two roles read the same table through different eyes, which is the whole
-- reason RLS is worth having rather than a WHERE clause in the repository.
--------------------------------------------------------------------------------
drop policy if exists outbox_dispatcher_access on app.outbox_message;
create policy outbox_dispatcher_access on app.outbox_message
  for all to mautodesk_dispatcher
  using (true) with check (true);

-- Pruning expired idempotency keys is also cross-tenant work, and also not
-- something the request path should be able to do in bulk.
drop policy if exists idempotency_dispatcher_access on app.idempotency_key;
create policy idempotency_dispatcher_access on app.idempotency_key
  for all to mautodesk_dispatcher
  using (true) with check (true);

--------------------------------------------------------------------------------
-- Grants. Narrow on purpose — this is the whole surface the role has.
--------------------------------------------------------------------------------
grant select, update on app.outbox_message   to mautodesk_dispatcher;
grant select, insert on app.processed_message to mautodesk_dispatcher;
grant select, delete on app.idempotency_key  to mautodesk_dispatcher;

-- The dispatcher never creates or destroys a message. It moves one from
-- "pending" to "dispatched" or "dead-lettered", and that is all.
revoke insert, delete on app.outbox_message from mautodesk_dispatcher;

--------------------------------------------------------------------------------
-- Verification: the dispatcher role must not be able to reach tenant data.
--
-- Asserted here, at migration time, rather than left to a test that someone
-- might not run. If a future grant widens this role by accident, the migration
-- that does it fails.
--------------------------------------------------------------------------------
do $$
declare
  leaked text;
begin
  select string_agg(format('%s.%s', table_schema, table_name), ', ' order by table_schema, table_name)
    into leaked
    from information_schema.role_table_grants
   where grantee = 'mautodesk_dispatcher'
     and not (table_schema = 'app'
              and table_name in ('outbox_message','processed_message','idempotency_key'));

  if leaked is not null then
    raise exception
      'mautodesk_dispatcher has been granted access beyond the outbox: %', leaked
      using errcode = 'insufficient_privilege';
  end if;

  raise notice 'mautodesk_dispatcher: access limited to the outbox and idempotency ledger';
end
$$;
