--------------------------------------------------------------------------------
-- MautoDesk — V0010 Compliance, lending, and titling
--
-- The schema foundation for DealerOS: the regulatory engine, the lender matrix,
-- and the title/lien/tag/pre-registration domain. The deal engine, Deal
-- Guardian, and the Oklahoma rule pack all bind to what is defined here, which
-- is why it lands before any of them.
--
-- Five structural commitments:
--
--  1. REGULATORY RULES ARE DATA, and they are NOT tax rules. sales.rule_set
--     stays what it is — versioned tax and fee arithmetic. compliance.* is a
--     separate engine for legal requirements, with a different lifecycle, a
--     different reviewer, and a different output (PASS/WARNING/BLOCK/REVIEW
--     rather than a number). Overloading one table with both would mean a
--     doc-fee-cap change and a Buyer's-Guide-requirement change share a version
--     number, which is wrong in both directions.
--
--  2. EVERY RULE VERSION CITES A SOURCE VERSION, and a source version carries
--     the retrieval date and a content hash. "Which rules applied to this deal
--     on 19 August 2026, and what did the statute say that day" must be
--     answerable from the database, not from someone's memory.
--
--  3. NOTHING IS ACTIVE WITHOUT HUMAN APPROVAL. approved_at IS NULL means the
--     evaluator cannot see the row — the same gate sales.rule_set already
--     applies to tax rules. A developer cannot approve a rule by writing code.
--
--  4. LENDING DATA IS TENANT-OWNED, NOT SHARED REFERENCE DATA. A dealer's buy
--     rates, program tiers, and reserve arrangements are commercially sensitive
--     and specific to that dealer's agreements. Publishing them as shared
--     reference data would leak one dealership's lender terms to a competitor
--     on the same platform. So lending.* carries tenant_id and RLS.
--
--  5. NO OKLAHOMA CONTENT IS SEEDED HERE. Not a deadline, not a fee cap, not a
--     hold type, not a tag expiry. Every one of those is a rule-version field
--     to be authored from a primary source and approved by a named reviewer.
--     This migration builds the container. Filling it is Phase 5.
--------------------------------------------------------------------------------

create schema if not exists compliance;  -- regulatory rules, sources, evaluations
create schema if not exists lending;     -- lenders, programs, submissions, decisions
create schema if not exists titling;     -- titles, holds, liens, tags, pre-registration

grant usage on schema compliance, lending, titling to mautodesk_app;

create extension if not exists btree_gist;

--------------------------------------------------------------------------------
-- The managed-schema registry.
--
-- V0006 applies RLS by looping over a hard-coded list of twelve schemas, and
-- app.rls_coverage_gaps() checks the same hard-coded list. That list cannot see
-- compliance, lending, or titling — so without this section, every table below
-- would be created with NO row-level security and the CI coverage gate would
-- report zero gaps while doing it. A silent hole, reported as healthy.
--
-- Rather than copy the list a third time and leave the next schema to hit the
-- same trap, the list becomes a table. V0006's behaviour is preserved exactly:
-- the registry is seeded with the twelve schemas it named, plus the three added
-- here. A future migration adds one row and calls app.apply_tenant_rls().
--------------------------------------------------------------------------------
create table if not exists app.managed_schema (
  schema_name text primary key,
  added_in    text not null,        -- migration that registered it
  notes       text
);

insert into app.managed_schema (schema_name, added_in, notes) values
  ('app',        'V0006', 'Cross-cutting infrastructure'),
  ('platform',   'V0006', 'Tenants, plans, platform administration'),
  ('identity',   'V0006', 'Users, roles, sessions, MFA'),
  ('inventory',  'V0006', 'Vehicles, photos, costs, recon'),
  ('crm',        'V0006', 'Customers, leads, tasks, activity'),
  ('sales',      'V0006', 'Deals, trades, jurisdictions, tax rule sets'),
  ('documents',  'V0006', 'Files, versions, templates'),
  ('signatures', 'V0006', 'Envelopes, signers, evidence'),
  ('messaging',  'V0006', 'Email and SMS threads'),
  ('publishing', 'V0006', 'Website and marketplace syndication'),
  ('billing',    'V0006', 'Subscription metering'),
  ('audit',      'V0006', 'The append-only ledger'),
  ('compliance', 'V0010', 'Regulatory rules, sources, evaluations, overrides'),
  ('lending',    'V0010', 'Lenders, programs, submissions, decisions'),
  ('titling',    'V0010', 'Titles, holds, liens, temporary tags, pre-registration')
on conflict (schema_name) do nothing;

comment on table app.managed_schema is
  'Schemas under the tenancy regime. app.apply_tenant_rls() and '
  'app.rls_coverage_gaps() both read this table. A new schema that is not '
  'registered here is invisible to both — which is a security hole, not a '
  'convenience. Register it in the same migration that creates it.';

-- Applies the V0006 policy shape to every tenant table in every managed schema.
-- Idempotent: safe to call from any later migration, and re-running it is how a
-- new schema gets protected without duplicating the loop.
create or replace function app.apply_tenant_rls() returns int
  language plpgsql as $$
declare
  r record;
  v_count int := 0;
begin
  for r in
    select c.table_schema, c.table_name
      from information_schema.columns c
      join information_schema.tables t
        on t.table_schema = c.table_schema and t.table_name = c.table_name
     where c.column_name = 'tenant_id'
       and t.table_type = 'BASE TABLE'
       and c.table_schema in (select schema_name from app.managed_schema)
       and not exists (
             select 1 from app.rls_exempt_table x
              where x.schema_name = c.table_schema and x.table_name = c.table_name)
     order by c.table_schema, c.table_name
  loop
    execute format('alter table %I.%I enable row level security', r.table_schema, r.table_name);
    execute format('alter table %I.%I force  row level security', r.table_schema, r.table_name);
    execute format('drop policy if exists %I on %I.%I',
                   r.table_name || '_tenant_isolation', r.table_schema, r.table_name);
    execute format(
      'create policy %I on %I.%I for all to mautodesk_app '
      'using (tenant_id = app.current_tenant_id()) '
      'with check (tenant_id = app.current_tenant_id())',
      r.table_name || '_tenant_isolation', r.table_schema, r.table_name);
    v_count := v_count + 1;
  end loop;
  return v_count;
end
$$;

comment on function app.apply_tenant_rls() is
  'Enables and FORCES RLS with the standard tenant-isolation policy on every '
  'tenant_id-bearing table in every registered schema. Returns the number of '
  'tables processed. Call it at the end of any migration that adds tables.';

--------------------------------------------------------------------------------
--------------------------------------------------------------------------------
-- COMPLIANCE — shared regulatory reference data.
--
-- Not tenant-owned: Oklahoma law is not a dealer's private information, and
-- every tenant in a jurisdiction must be evaluated against the identical rule
-- version. These tables are read-only to the application role; authoring
-- happens through the regulatory admin path, which runs as a separate role.
--------------------------------------------------------------------------------
create table compliance.regulatory_jurisdiction (
  code          text primary key,          -- 'US', 'US-OK', 'US-OK-TULSA'
  parent_code   text references compliance.regulatory_jurisdiction(code),
  level         text not null check (level in ('federal','state','county','city')),
  name          text not null,
  is_active     boolean not null default true,
  created_at    timestamptz not null default now()
);

comment on table compliance.regulatory_jurisdiction is
  'Deliberately separate from sales.tax_jurisdiction. A taxing jurisdiction is '
  'a rate-bearing geography; a regulatory jurisdiction is a law-making body. '
  'They overlap in Oklahoma and diverge elsewhere, and conflating them would '
  'mean a city sales-tax boundary change could silently move a titling rule.';

create table compliance.regulatory_authority (
  id                uuid primary key default gen_random_uuid(),
  jurisdiction_code text not null references compliance.regulatory_jurisdiction(code),
  code              text not null,         -- e.g. an agency short code
  name              text not null,
  website_url       text,
  notes             text,
  created_at        timestamptz not null default now()
);

create unique index regulatory_authority_uq
  on compliance.regulatory_authority (jurisdiction_code, code);

-- A source is a document: a statute, an administrative rule, a published
-- bulletin, a form's instructions. It is identified once; its CONTENT is
-- versioned separately, because the document keeps its identity when amended.
create table compliance.regulatory_source (
  id            uuid primary key default gen_random_uuid(),
  authority_id  uuid not null references compliance.regulatory_authority(id),
  source_type   text not null
                check (source_type in ('statute','administrative_rule','bulletin',
                                       'form_instruction','published_guidance','other')),
  title         text not null,
  citation      text not null,             -- e.g. a statutory or OAC citation
  url           text,
  is_active     boolean not null default true,
  created_at    timestamptz not null default now()
);

create unique index regulatory_source_citation_uq
  on compliance.regulatory_source (authority_id, citation);

-- One retrieval of one source. The content hash is what makes "the rule was
-- based on this text" a verifiable claim rather than an assertion.
create table compliance.regulatory_source_version (
  id               uuid primary key default gen_random_uuid(),
  source_id        uuid not null references compliance.regulatory_source(id),
  version          int not null,
  retrieved_at     timestamptz not null,
  retrieved_by     text not null,          -- named person or process
  content_hash     bytea not null,         -- sha256 of the retrieved document
  content_location text,                   -- object-store key of the archived copy
  effective_from   date,                   -- when the SOURCE took effect, if stated
  change_summary   text,
  last_verified_at timestamptz,            -- last time someone confirmed it is current
  created_at       timestamptz not null default now()
);

create unique index regulatory_source_version_uq
  on compliance.regulatory_source_version (source_id, version);
create index regulatory_source_version_stale_ix
  on compliance.regulatory_source_version (last_verified_at nulls first);

create trigger regulatory_source_version_immutable
  before update or delete on compliance.regulatory_source_version
  for each row execute function app.deny_mutation();

comment on table compliance.regulatory_source_version is
  'Append-only. A re-retrieval is a new version, so the text a 2026 rule was '
  'written against survives the statute being amended in 2028.';

-- The rule's stable identity. Its behaviour lives in the versions.
create table compliance.regulatory_rule (
  id                uuid primary key default gen_random_uuid(),
  rule_code         text not null,         -- e.g. 'OK-TEMP-TAG-001'
  jurisdiction_code text not null references compliance.regulatory_jurisdiction(code),
  category          text not null
                    check (category in ('licensing','documentation','odometer','disclosure',
                                        'title','lien','registration','temporary_tag',
                                        'financing','advertising','tax','privacy','other')),
  title             text not null,
  description       text,
  created_at        timestamptz not null default now()
);

create unique index regulatory_rule_code_uq on compliance.regulatory_rule (rule_code);
create index regulatory_rule_jurisdiction_ix
  on compliance.regulatory_rule (jurisdiction_code, category);

-- The evaluated artefact. Append-only and effective-dated: a historical deal
-- keeps resolving the version that applied on its own date, forever.
create table compliance.regulatory_rule_version (
  id                uuid primary key default gen_random_uuid(),
  rule_id           uuid not null references compliance.regulatory_rule(id),
  version           text not null,         -- semver, e.g. '1.1.0'
  status            text not null default 'draft'
                    check (status in ('draft','review','approved','scheduled','active','retired')),
  effective_from    date not null,
  effective_to      date,
  source_version_id uuid not null references compliance.regulatory_source_version(id),

  -- What makes the rule fire, and what it does. JSON, validated against a
  -- versioned JSON Schema in the application and deserialized into typed
  -- conditions by the evaluator — the same "flexible in storage, typed in the
  -- calculation" posture sales.rule_set takes. Deliberately NOT a bespoke
  -- language: field path, operator, value, and all/any grouping is the whole
  -- vocabulary, because a rule nobody can read is a rule nobody can audit.
  trigger_event     text not null,         -- e.g. 'DEAL_SOLD', 'DEAL_CONTRACTED'
  definition_schema int not null default 1,
  definition        jsonb not null,
  severity          text not null
                    check (severity in ('blocking','warning','review','informational')),

  approved_at       timestamptz,
  approved_by       text,                  -- named reviewer: counsel or compliance officer
  approval_note     text,
  retired_at        timestamptz,
  created_at        timestamptz not null default now(),
  created_by        text,

  constraint rule_version_period_ck
    check (effective_to is null or effective_to > effective_from),
  -- An approved rule without a named approver is an approval that nobody owns.
  constraint rule_version_approval_ck
    check ((approved_at is null) = (approved_by is null))
);

create unique index regulatory_rule_version_uq
  on compliance.regulatory_rule_version (rule_id, version);
create index regulatory_rule_version_resolve_ix
  on compliance.regulatory_rule_version (rule_id, effective_from desc)
  where approved_at is not null;
create index regulatory_rule_version_trigger_ix
  on compliance.regulatory_rule_version (trigger_event, effective_from desc)
  where approved_at is not null;

-- No two approved versions of one rule may cover the same date. Without this,
-- the resolver could legitimately pick either, and two identical deals could
-- get different compliance outcomes. Same GiST pattern as sales.rule_set.
alter table compliance.regulatory_rule_version
  add constraint regulatory_rule_version_no_overlap
  exclude using gist (
    rule_id with =,
    daterange(effective_from, coalesce(effective_to, 'infinity'::date), '[)') with &&
  ) where (approved_at is not null);

create trigger regulatory_rule_version_immutable
  before update or delete on compliance.regulatory_rule_version
  for each row execute function app.deny_mutation();

comment on table compliance.regulatory_rule_version is
  'Append-only and effective-dated. A correction is a new version, never an '
  'edit — because editing one would silently restate what the engine told a '
  'dealer last year. approved_at IS NULL makes the row invisible to the '
  'evaluator, so an unreviewed rule cannot block or permit anything.';

-- Every rule ships with its tests. A rule version with no failing-case test has
-- not been thought through, and the CI regulatory gate treats it as such.
create table compliance.regulatory_rule_test (
  id               uuid primary key default gen_random_uuid(),
  rule_version_id  uuid not null references compliance.regulatory_rule_version(id),
  name             text not null,
  expectation      text not null check (expectation in ('pass','warning','block','review')),
  input            jsonb not null,
  expected_detail  jsonb,
  created_at       timestamptz not null default now()
);

create index regulatory_rule_test_version_ix
  on compliance.regulatory_rule_test (rule_version_id, expectation);

-- Business-day arithmetic for deadlines. Seeded empty on purpose: a holiday
-- calendar is regulatory content, and inventing one would put a wrong
-- submission deadline in front of a dealer.
create table compliance.jurisdiction_holiday (
  jurisdiction_code text not null references compliance.regulatory_jurisdiction(code),
  holiday_on        date not null,
  name              text not null,
  source_version_id uuid references compliance.regulatory_source_version(id),
  primary key (jurisdiction_code, holiday_on)
);

comment on table compliance.jurisdiction_holiday is
  'Empty until populated from a cited source. Business-day deadline maths must '
  'fail loudly rather than assume a five-day week with no holidays.';

--------------------------------------------------------------------------------
-- COMPLIANCE — tenant-owned evaluation records.
--------------------------------------------------------------------------------

-- One evaluation of one deal against one resolved rule set.
create table compliance.compliance_run (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  deal_id            uuid references sales.deal(id) on delete cascade,
  vehicle_id         uuid references inventory.vehicle(id),
  jurisdiction_code  text not null references compliance.regulatory_jurisdiction(code),
  trigger_event      text not null,
  engine_version     text not null,        -- semver of the regulatory evaluator
  rule_pack_version  text not null,        -- the resolved pack, distinct from any one rule
  evaluated_on       date not null,        -- the date the rules were resolved AS OF
  executed_at        timestamptz not null default now(),
  executed_by        uuid,

  overall_status     text not null check (overall_status in ('green','yellow','red')),
  blocking_count     int not null default 0,
  warning_count      int not null default 0,

  -- Reproducibility: the same input hash under the same engine and pack version
  -- must produce the same result hash. A mismatch is a defect, and a detectable
  -- one, which is the point.
  input_hash         bytea not null,
  result_hash        bytea not null,
  input              jsonb not null
);

create index compliance_run_deal_ix
  on compliance.compliance_run (tenant_id, deal_id, executed_at desc);
create index compliance_run_status_ix
  on compliance.compliance_run (tenant_id, overall_status, executed_at desc);

create trigger compliance_run_immutable before update or delete on compliance.compliance_run
  for each row execute function app.deny_mutation();
create trigger compliance_run_enforce_tenant before insert or update on compliance.compliance_run
  for each row execute function app.enforce_tenant();

-- One rule's verdict within a run. This row is what Deal Guardian shows the
-- user and what an AI explanation is required to cite. It is never generated.
create table compliance.compliance_result (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  run_id             uuid not null references compliance.compliance_run(id) on delete cascade,
  rule_code          text not null,
  rule_version       text not null,
  rule_version_id    uuid not null references compliance.regulatory_rule_version(id),
  source_version_id  uuid not null references compliance.regulatory_source_version(id),
  status             text not null check (status in ('pass','warning','block','review')),
  severity           text not null
                     check (severity in ('blocking','warning','review','informational')),
  message            text not null,
  evaluation_detail  jsonb,                -- which condition matched, with the values seen
  override_id        uuid,                 -- set when an approved override suppressed a block
  sort_order         int not null default 0
);

create index compliance_result_run_ix
  on compliance.compliance_result (tenant_id, run_id, sort_order);
create index compliance_result_blocking_ix
  on compliance.compliance_result (tenant_id, run_id) where status = 'block';

create trigger compliance_result_immutable before update or delete on compliance.compliance_result
  for each row execute function app.deny_mutation();
create trigger compliance_result_enforce_tenant before insert or update on compliance.compliance_result
  for each row execute function app.enforce_tenant();

-- Documents the rules say this deal needs. Derived from a run, not hand-listed,
-- so adding a state pack adds its paperwork without a code change.
create table compliance.document_requirement (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  deal_id             uuid not null references sales.deal(id) on delete cascade,
  run_id              uuid references compliance.compliance_run(id),
  code                text not null,
  name                text not null,
  rule_code           text,
  rule_version        text,
  is_blocking         boolean not null default true,
  status              text not null default 'required'
                      check (status in ('required','generated','signed','waived','not_applicable')),
  satisfied_document_id uuid references documents.document(id),
  satisfied_at        timestamptz,
  waived_override_id  uuid,
  created_at          timestamptz not null default now(),
  updated_at          timestamptz not null default now()
);

create unique index document_requirement_deal_code_uq
  on compliance.document_requirement (tenant_id, deal_id, code);
create index document_requirement_outstanding_ix
  on compliance.document_requirement (tenant_id, deal_id)
  where status in ('required','generated');

create trigger document_requirement_set_updated_at
  before update on compliance.document_requirement
  for each row execute function app.set_updated_at();
create trigger document_requirement_enforce_tenant
  before insert or update on compliance.document_requirement
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Overrides.
--
-- Split into a request and an append-only decision log, deliberately. A single
-- mutable row with a status column would let an approval be quietly rewritten;
-- a single append-only row could never be approved after being requested. The
-- request carries current state, the decision log carries history, and neither
-- can be edited into saying something that did not happen.
--
-- There is no path that suppresses a blocking rule without a row in BOTH.
--------------------------------------------------------------------------------
create table compliance.override_request (
  id               uuid primary key default gen_random_uuid(),
  tenant_id        uuid not null references platform.tenant(id),
  deal_id          uuid not null references sales.deal(id) on delete cascade,
  rule_code        text not null,
  rule_version     text not null,
  rule_version_id  uuid not null references compliance.regulatory_rule_version(id),
  reason           text not null check (length(btrim(reason)) >= 20),
  requested_by     uuid not null references identity."user"(id),
  requested_at     timestamptz not null default now(),
  status           text not null default 'pending'
                   check (status in ('pending','approved','rejected','revoked','expired')),
  expires_at       timestamptz,
  updated_at       timestamptz not null default now()
);

create index override_request_deal_ix
  on compliance.override_request (tenant_id, deal_id, requested_at desc);
create index override_request_pending_ix
  on compliance.override_request (tenant_id, status) where status = 'pending';
-- One live override per rule per deal. A second one is a second decision.
create unique index override_request_active_uq
  on compliance.override_request (tenant_id, deal_id, rule_code) where status = 'approved';

create trigger override_request_set_updated_at before update on compliance.override_request
  for each row execute function app.set_updated_at();
create trigger override_request_enforce_tenant before insert or update on compliance.override_request
  for each row execute function app.enforce_tenant();

comment on column compliance.override_request.reason is
  'Twenty characters minimum, enforced by check constraint. "ok" is not a '
  'reason, and the constraint is there so nobody has to argue about it in review.';

create table compliance.override_decision (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  request_id   uuid not null references compliance.override_request(id) on delete cascade,
  action       text not null check (action in ('approved','rejected','revoked','expired')),
  decided_by   uuid references identity."user"(id),   -- null only for system expiry
  decided_at   timestamptz not null default now(),
  note         text,
  expires_at   timestamptz,
  -- The approver must be a different person from the requester. Enforced in the
  -- application because this table cannot see the request's requested_by, and
  -- asserted by an integration test rather than trusted to review.
  constraint override_decision_actor_ck
    check (action = 'expired' or decided_by is not null)
);

create index override_decision_request_ix
  on compliance.override_decision (tenant_id, request_id, decided_at);

create trigger override_decision_immutable before update or delete on compliance.override_decision
  for each row execute function app.deny_mutation();
create trigger override_decision_enforce_tenant
  before insert or update on compliance.override_decision
  for each row execute function app.enforce_tenant();

alter table compliance.compliance_result
  add constraint compliance_result_override_fk
  foreign key (override_id) references compliance.override_request(id);
alter table compliance.document_requirement
  add constraint document_requirement_override_fk
  foreign key (waived_override_id) references compliance.override_request(id);

--------------------------------------------------------------------------------
--------------------------------------------------------------------------------
-- LENDING
--
-- Tenant-owned throughout (commitment 4 above). A dealer's lender list, its buy
-- rates, and its reserve arrangements are that dealer's commercial terms.
--------------------------------------------------------------------------------
create table lending.lender (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  code         text not null,
  name         text not null,
  type         text not null
               check (type in ('captive','bank','credit_union','finance_company',
                               'buy_here_pay_here','other')),
  contact_name text,
  contact_email text,
  contact_phone text,
  portal_url   text,
  is_active    boolean not null default true,
  notes        text,
  created_at   timestamptz not null default now(),
  created_by   uuid,
  updated_at   timestamptz not null default now(),
  updated_by   uuid,
  deleted_at   timestamptz
);

create unique index lender_tenant_code_uq
  on lending.lender (tenant_id, code) where deleted_at is null;
create index lender_active_ix on lending.lender (tenant_id, name) where is_active and deleted_at is null;

create trigger lender_set_updated_at before update on lending.lender
  for each row execute function app.set_updated_at();
create trigger lender_enforce_tenant before insert or update on lending.lender
  for each row execute function app.enforce_tenant();

create table lending.lender_program (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  lender_id    uuid not null references lending.lender(id) on delete cascade,
  code         text not null,
  name         text not null,
  credit_tier  text,                       -- dealer's own tier label; not a bureau score
  is_active    boolean not null default true,
  created_at   timestamptz not null default now(),
  created_by   uuid,
  updated_at   timestamptz not null default now(),
  deleted_at   timestamptz
);

create unique index lender_program_code_uq
  on lending.lender_program (tenant_id, lender_id, code) where deleted_at is null;

create trigger lender_program_set_updated_at before update on lending.lender_program
  for each row execute function app.set_updated_at();
create trigger lender_program_enforce_tenant before insert or update on lending.lender_program
  for each row execute function app.enforce_tenant();

-- The version is the unit a deal binds to, so a rate-sheet change in March
-- cannot restate what a January deal was quoted under.
--
-- `eligibility` and `rate_sheet` are jsonb rather than a table of typed rule
-- rows. Master-prompt Step 14 suggests a lender_program_rules table; that would
-- create a second source of truth alongside the structured document the engine
-- actually deserializes, and this repository's doctrine (CLAUDE.md, ADR-0008)
-- is that a number with two homes is a number with none. Same posture as
-- sales.rule_set: flexible in storage, strongly typed in the calculation,
-- validated against a versioned JSON Schema on the way in.
create table lending.lender_program_version (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references platform.tenant(id),
  program_id        uuid not null references lending.lender_program(id) on delete cascade,
  version           int not null,
  effective_from    date not null,
  effective_to      date,

  -- Which arithmetic this lender uses. Not every lender amortizes the same way,
  -- and the deal engine selects its IPaymentMethod strategy from this field —
  -- so a replayed historical deal recomputes the way it was originally computed.
  payment_method    text not null default 'standard_amortization'
                    check (payment_method in ('standard_amortization','simple_interest',
                                              'precomputed','lease','other')),
  finance_charge_method text,

  eligibility_schema int not null default 1,
  eligibility        jsonb not null,   -- LTV/PTI/term/age/mileage/geography caps
  rate_sheet         jsonb not null,   -- buy rates by tier and term
  reserve_terms      jsonb,            -- dealer participation; disclosure-sensitive

  approved_at       timestamptz,
  approved_by       text,
  source_note       text,              -- program bulletin or agreement reference
  created_at        timestamptz not null default now(),
  created_by        uuid,

  constraint lender_program_version_period_ck
    check (effective_to is null or effective_to > effective_from),
  constraint lender_program_version_approval_ck
    check ((approved_at is null) = (approved_by is null))
);

create unique index lender_program_version_uq
  on lending.lender_program_version (program_id, version);
create index lender_program_version_effective_ix
  on lending.lender_program_version (tenant_id, program_id, effective_from desc)
  where approved_at is not null;

alter table lending.lender_program_version
  add constraint lender_program_version_no_overlap
  exclude using gist (
    program_id with =,
    daterange(effective_from, coalesce(effective_to, 'infinity'::date), '[)') with &&
  ) where (approved_at is not null);

create trigger lender_program_version_immutable
  before update or delete on lending.lender_program_version
  for each row execute function app.deny_mutation();
create trigger lender_program_version_enforce_tenant
  before insert or update on lending.lender_program_version
  for each row execute function app.enforce_tenant();

comment on column lending.lender_program_version.reserve_terms is
  'Rate participation is regulated and disclosure-sensitive, like '
  'sales.finance_terms.buy_rate. Any UI exposing it is gated behind '
  'sales.deal.finance.read and audited.';

--------------------------------------------------------------------------------
-- Submissions and decisions.
--
-- v1 records a submission; it does not transmit one. No lender API is assumed,
-- invented, or stubbed here. When a real integration exists it fills in the
-- payload columns; until then a finance manager records what was sent through
-- the lender's own portal, and the deal history is still complete.
--------------------------------------------------------------------------------
create table lending.lender_submission (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  deal_id             uuid not null references sales.deal(id) on delete cascade,
  program_version_id  uuid not null references lending.lender_program_version(id),
  calculation_id      uuid references sales.deal_calculation(id),
  channel             text not null default 'manual'
                      check (channel in ('manual','portal','api','file')),
  status              text not null default 'draft'
                      check (status in ('draft','queued','submitted','failed',
                                        'decisioned','withdrawn')),
  -- Idempotency is not optional on anything that leaves the building. A retried
  -- submit must not become a second credit application.
  idempotency_key     text,
  attempt_count       int not null default 0,
  submitted_at        timestamptz,
  request_payload     jsonb,
  response_payload    jsonb,
  external_reference  text,
  failure_reason      text,
  created_at          timestamptz not null default now(),
  created_by          uuid,
  updated_at          timestamptz not null default now()
);

create index lender_submission_deal_ix
  on lending.lender_submission (tenant_id, deal_id, created_at desc);
create unique index lender_submission_idempotency_uq
  on lending.lender_submission (tenant_id, idempotency_key)
  where idempotency_key is not null;

create trigger lender_submission_set_updated_at before update on lending.lender_submission
  for each row execute function app.set_updated_at();
create trigger lender_submission_enforce_tenant
  before insert or update on lending.lender_submission
  for each row execute function app.enforce_tenant();

create table lending.lender_decision (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  submission_id      uuid not null references lending.lender_submission(id) on delete cascade,
  decision           text not null
                     check (decision in ('approved','conditionally_approved','countered',
                                         'declined','pending','withdrawn')),
  decided_at         timestamptz not null default now(),
  recorded_by        uuid,
  approved_amount    numeric(14,2),
  max_advance        numeric(14,2),
  apr                numeric(9,6),
  buy_rate           numeric(9,6),
  term_months        int check (term_months > 0),
  max_back_end       numeric(14,2),
  reserve_amount     numeric(14,2),
  decline_reason     text,
  response_payload   jsonb,
  external_reference text
);

create index lender_decision_submission_ix
  on lending.lender_decision (tenant_id, submission_id, decided_at desc);

create trigger lender_decision_immutable before update or delete on lending.lender_decision
  for each row execute function app.deny_mutation();
create trigger lender_decision_enforce_tenant before insert or update on lending.lender_decision
  for each row execute function app.enforce_tenant();

comment on table lending.lender_decision is
  'Append-only. A lender changing its mind is a new decision row, not an edit — '
  'because "what were we approved for on Tuesday" is a question that gets asked '
  'when a deal unwinds.';

create table lending.lender_stipulation (
  id                    uuid primary key default gen_random_uuid(),
  tenant_id             uuid not null references platform.tenant(id),
  decision_id           uuid not null references lending.lender_decision(id) on delete cascade,
  code                  text,
  description           text not null,
  is_funding_blocking   boolean not null default true,
  status                text not null default 'outstanding'
                        check (status in ('outstanding','satisfied','waived')),
  satisfied_document_id uuid references documents.document(id),
  satisfied_at          timestamptz,
  satisfied_by          uuid,
  created_at            timestamptz not null default now(),
  updated_at            timestamptz not null default now()
);

create index lender_stipulation_decision_ix
  on lending.lender_stipulation (tenant_id, decision_id);
create index lender_stipulation_outstanding_ix
  on lending.lender_stipulation (tenant_id, status) where status = 'outstanding';

create trigger lender_stipulation_set_updated_at before update on lending.lender_stipulation
  for each row execute function app.set_updated_at();
create trigger lender_stipulation_enforce_tenant
  before insert or update on lending.lender_stipulation
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
--------------------------------------------------------------------------------
-- TITLING
--
-- State-neutral by construction. There is no Oklahoma column here and no
-- Oklahoma value seeded. `title_type`, `status`, and hold types are open text
-- with a constrained core vocabulary, because the set of holds a state can
-- place on a title is regulatory content, not a schema decision — and hard-
-- coding one state's list is precisely what makes adding Texas a rewrite.
--------------------------------------------------------------------------------
create table titling.vehicle_title (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  vehicle_id     uuid not null references inventory.vehicle(id) on delete cascade,
  deal_id        uuid references sales.deal(id),

  -- Deliberately includes 'unknown'. A dealer who has not yet established
  -- whether a title is electronic or paper must be able to say so, because the
  -- alternative is a default that silently becomes an assumption.
  title_type     text not null default 'unknown'
                 check (title_type in ('paper','electronic','mco','unknown')),
  issuing_state  char(2),
  title_number   text,
  status         text not null default 'unknown'
                 check (status in ('unknown','verified','pending','held','transfer_pending',
                                   'transferred','lien_active','released')),
  origin         text check (origin in ('trade','auction','floorplan','consignment',
                                        'out_of_state','manufacturer','other')),
  is_in_hand     boolean not null default false,
  issued_on      date,
  received_at    timestamptz,
  verified_at    timestamptz,
  verified_by    uuid,
  notes          text,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now(),
  updated_by     uuid
);

create unique index vehicle_title_vehicle_uq on titling.vehicle_title (tenant_id, vehicle_id);
create index vehicle_title_deal_ix on titling.vehicle_title (tenant_id, deal_id);
create index vehicle_title_status_ix on titling.vehicle_title (tenant_id, status);

create trigger vehicle_title_set_updated_at before update on titling.vehicle_title
  for each row execute function app.set_updated_at();
create trigger vehicle_title_enforce_tenant before insert or update on titling.vehicle_title
  for each row execute function app.enforce_tenant();

comment on column titling.vehicle_title.title_type is
  'Never defaulted from the issuing state. Whether a given transaction produces '
  'an electronic or a paper title depends on transaction type, dealer type, '
  'lien status and statutory exceptions — which is a rule-engine question, not '
  'a column default.';

create table titling.title_hold (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  title_id            uuid not null references titling.vehicle_title(id) on delete cascade,
  hold_type           text not null,        -- open vocabulary; see table comment
  reason              text,
  required_action     text,
  required_documentation text,
  is_blocking         boolean not null default true,
  rule_code           text,
  rule_version        text,
  source_version_id   uuid references compliance.regulatory_source_version(id),
  opened_at           timestamptz not null default now(),
  opened_by           uuid,
  cleared_at          timestamptz,
  cleared_by          uuid,
  cleared_note        text
);

create index title_hold_title_ix on titling.title_hold (tenant_id, title_id);
create index title_hold_open_ix
  on titling.title_hold (tenant_id, title_id) where cleared_at is null;

create trigger title_hold_enforce_tenant before insert or update on titling.title_hold
  for each row execute function app.enforce_tenant();

comment on column titling.title_hold.hold_type is
  'Free text with a rule-supplied vocabulary rather than a check constraint. '
  'The list of holds a jurisdiction can place on a title changes by bulletin, '
  'and a check constraint would turn that into a migration and a deployment.';

create table titling.vin_inspection (
  id                    uuid primary key default gen_random_uuid(),
  tenant_id             uuid not null references platform.tenant(id),
  vehicle_id            uuid not null references inventory.vehicle(id) on delete cascade,
  title_id              uuid references titling.vehicle_title(id) on delete cascade,
  required_by_rule_code text,
  required_by_rule_version text,
  status                text not null default 'required'
                        check (status in ('required','scheduled','passed','failed','waived')),
  scheduled_for         timestamptz,
  inspected_at          timestamptz,
  inspector_name        text,
  inspection_location   text,
  certificate_reference text,
  document_id           uuid references documents.document(id),
  failure_reason        text,
  created_at            timestamptz not null default now(),
  updated_at            timestamptz not null default now()
);

create index vin_inspection_vehicle_ix on titling.vin_inspection (tenant_id, vehicle_id);
create index vin_inspection_outstanding_ix
  on titling.vin_inspection (tenant_id, status) where status in ('required','scheduled');

create trigger vin_inspection_set_updated_at before update on titling.vin_inspection
  for each row execute function app.set_updated_at();
create trigger vin_inspection_enforce_tenant before insert or update on titling.vin_inspection
  for each row execute function app.enforce_tenant();

create table titling.lien (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  vehicle_id          uuid not null references inventory.vehicle(id) on delete cascade,
  title_id            uuid references titling.vehicle_title(id) on delete cascade,
  deal_id             uuid references sales.deal(id),
  lienholder_name     text not null,
  lienholder_reference text,               -- the state's identifier for the lienholder
  lien_type           text not null default 'unknown'
                      check (lien_type in ('electronic','paper','unknown')),
  position            int not null default 1 check (position > 0),
  status              text not null default 'pending'
                      check (status in ('pending','active','released','rejected','cancelled')),
  perfected_on        date,
  released_on         date,
  release_document_id uuid references documents.document(id),
  notes               text,
  created_at          timestamptz not null default now(),
  created_by          uuid,
  updated_at          timestamptz not null default now()
);

create index lien_vehicle_ix on titling.lien (tenant_id, vehicle_id);
create index lien_active_ix on titling.lien (tenant_id, status) where status = 'active';

create trigger lien_set_updated_at before update on titling.lien
  for each row execute function app.set_updated_at();
create trigger lien_enforce_tenant before insert or update on titling.lien
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Outbound state transactions: title applications, transfers, lien filings and
-- releases, pre-registration submissions.
--
-- The transport is UNDECIDED. Whether a jurisdiction offers an API, a file
-- exchange, or neither is a question to be answered with that jurisdiction's
-- dealer support, not assumed here. So these tables model the RECORD of a
-- submission — payloads, references, failures, attempts — and the adapter that
-- eventually carries it is a port implementation, not a schema change.
--------------------------------------------------------------------------------
create table titling.title_transaction (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  title_id           uuid not null references titling.vehicle_title(id) on delete cascade,
  deal_id            uuid references sales.deal(id),
  kind               text not null
                     check (kind in ('application','transfer','correction','duplicate',
                                     'repossession','salvage','other')),
  channel            text not null default 'manual'
                     check (channel in ('manual','portal','api','file')),
  status             text not null default 'draft'
                     check (status in ('draft','queued','submitted','accepted',
                                       'rejected','failed','cancelled')),
  idempotency_key    text,
  attempt_count      int not null default 0,
  submitted_at       timestamptz,
  completed_at       timestamptz,
  submission_payload jsonb,
  response_payload   jsonb,
  external_reference text,
  failure_reason     text,
  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now()
);

create index title_transaction_title_ix
  on titling.title_transaction (tenant_id, title_id, created_at desc);
create unique index title_transaction_idempotency_uq
  on titling.title_transaction (tenant_id, idempotency_key)
  where idempotency_key is not null;

create trigger title_transaction_set_updated_at before update on titling.title_transaction
  for each row execute function app.set_updated_at();
create trigger title_transaction_enforce_tenant
  before insert or update on titling.title_transaction
  for each row execute function app.enforce_tenant();

create table titling.lien_transaction (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  lien_id            uuid not null references titling.lien(id) on delete cascade,
  kind               text not null check (kind in ('add','release','amend','cancel')),
  channel            text not null default 'manual'
                     check (channel in ('manual','portal','api','file')),
  status             text not null default 'draft'
                     check (status in ('draft','queued','submitted','accepted',
                                       'rejected','failed','cancelled')),
  idempotency_key    text,
  attempt_count      int not null default 0,
  submitted_at       timestamptz,
  completed_at       timestamptz,
  submission_payload jsonb,
  response_payload   jsonb,
  external_reference text,
  failure_reason     text,
  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now()
);

create index lien_transaction_lien_ix
  on titling.lien_transaction (tenant_id, lien_id, created_at desc);
create unique index lien_transaction_idempotency_uq
  on titling.lien_transaction (tenant_id, idempotency_key)
  where idempotency_key is not null;

create trigger lien_transaction_set_updated_at before update on titling.lien_transaction
  for each row execute function app.set_updated_at();
create trigger lien_transaction_enforce_tenant
  before insert or update on titling.lien_transaction
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Pre-registration.
--
-- The submission deadline is a COLUMN, not a constant: computed once by the
-- regulatory engine from the rule version in force on the sale date, then
-- frozen with the rule code and version that produced it. A later rule change
-- must not retroactively move a deadline a dealer already met.
--------------------------------------------------------------------------------
create table titling.pre_registration (
  id                       uuid primary key default gen_random_uuid(),
  tenant_id                uuid not null references platform.tenant(id),
  deal_id                  uuid not null references sales.deal(id) on delete cascade,
  vehicle_id               uuid not null references inventory.vehicle(id),
  customer_id              uuid references crm.customer(id),

  sale_date                date not null,
  submission_deadline      timestamptz,
  deadline_rule_code       text,
  deadline_rule_version    text,

  buyer_registration_state char(2),
  buyer_type               text check (buyer_type in ('individual','business','government',
                                                      'dealer','out_of_state','other')),
  licensed_operator        text,           -- the licensed operator submitting, where required

  channel                  text not null default 'manual'
                           check (channel in ('manual','portal','api','file')),
  status                   text not null default 'pending'
                           check (status in ('pending','queued','submitted','accepted',
                                             'rejected','failed','not_required','cancelled')),
  idempotency_key          text,
  attempt_count            int not null default 0,
  submitted_at             timestamptz,
  completed_at             timestamptz,
  submission_payload       jsonb,
  response_payload         jsonb,
  external_reference       text,
  failure_reason           text,
  created_at               timestamptz not null default now(),
  created_by               uuid,
  updated_at               timestamptz not null default now()
);

create unique index pre_registration_deal_uq on titling.pre_registration (tenant_id, deal_id);
create index pre_registration_due_ix
  on titling.pre_registration (tenant_id, submission_deadline)
  where status in ('pending','queued','failed');
create unique index pre_registration_idempotency_uq
  on titling.pre_registration (tenant_id, idempotency_key)
  where idempotency_key is not null;

create trigger pre_registration_set_updated_at before update on titling.pre_registration
  for each row execute function app.set_updated_at();
create trigger pre_registration_enforce_tenant
  before insert or update on titling.pre_registration
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Temporary tags.
--
-- Expiry is engine-generated, like the pre-registration deadline, and frozen
-- with the rule that produced it. The status vocabulary is the full lifecycle
-- from master-prompt Step 24, because a tag that was voided and replaced is a
-- fact somebody will eventually need to prove.
--------------------------------------------------------------------------------
create table titling.temporary_tag (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  tag_number         text not null,
  vehicle_id         uuid references inventory.vehicle(id),
  deal_id            uuid references sales.deal(id),
  customer_id        uuid references crm.customer(id),
  issuing_state      char(2),
  vendor             text,
  status             text not null default 'available'
                     check (status in ('available','assigned','issued','submitted',
                                       'expired','void','replaced')),
  assigned_at        timestamptz,
  issued_at          timestamptz,
  expires_at         timestamptz,
  expiry_rule_code   text,
  expiry_rule_version text,
  submission_status  text check (submission_status in ('not_required','pending','submitted',
                                                       'accepted','rejected','failed')),
  external_reference text,
  replaced_by_id     uuid references titling.temporary_tag(id),
  void_reason        text,
  voided_at          timestamptz,
  voided_by          uuid,
  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now(),

  constraint temporary_tag_void_ck
    check (status <> 'void' or (void_reason is not null and voided_at is not null))
);

-- One live tag per number. A number can be reused only after the prior tag has
-- left the live states, which is what makes a retried issuance safe.
create unique index temporary_tag_live_uq
  on titling.temporary_tag (tenant_id, tag_number)
  where status in ('assigned','issued','submitted');
create index temporary_tag_deal_ix on titling.temporary_tag (tenant_id, deal_id);
create index temporary_tag_expiring_ix
  on titling.temporary_tag (tenant_id, expires_at) where status in ('issued','submitted');
-- One live tag per deal. A second one means the first was voided or replaced.
create unique index temporary_tag_deal_live_uq
  on titling.temporary_tag (tenant_id, deal_id)
  where deal_id is not null and status in ('assigned','issued','submitted');

create trigger temporary_tag_set_updated_at before update on titling.temporary_tag
  for each row execute function app.set_updated_at();
create trigger temporary_tag_enforce_tenant before insert or update on titling.temporary_tag
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
--------------------------------------------------------------------------------
-- sales.deal_calculation — provenance columns (additive, expand-only).
--
-- The table already records which TAX rule set produced its numbers. Replaying
-- a deal exactly also needs the regulatory pack and the lender program that
-- were in force, since both can change the arithmetic. Nullable because every
-- existing row predates them and backfilling a value nobody recorded would be
-- inventing history.
--------------------------------------------------------------------------------
alter table sales.deal_calculation
  add column if not exists jurisdiction_code           text,
  add column if not exists regulatory_rule_pack_version text,
  add column if not exists lender_program_version_id    uuid
    references lending.lender_program_version(id);

comment on column sales.deal_calculation.jurisdiction_code is
  'The regulatory jurisdiction ("US-OK"), distinct from the taxing jurisdiction '
  'already referenced through rule_set_id.';
comment on column sales.deal_calculation.lender_program_version_id is
  'Null for cash deals. Non-null pins the payment method and rate sheet used, '
  'so a replay does not recompute a 2026 payment with a 2029 rate sheet.';

--------------------------------------------------------------------------------
-- RLS, exemptions, and grants.
--------------------------------------------------------------------------------

-- Shared regulatory reference data. Same standard as sales.rule_set: public
-- law, identical for every tenant, and read-only to the application.
insert into app.rls_exempt_table (schema_name, table_name, reason) values
  ('compliance','regulatory_jurisdiction',    'Jurisdiction reference data; identical for every tenant'),
  ('compliance','regulatory_authority',       'Public agency reference data'),
  ('compliance','regulatory_source',          'Published law and guidance; source-cited public record'),
  ('compliance','regulatory_source_version',  'Archived text of published law; public record'),
  ('compliance','regulatory_rule',            'Rule identity for public law; identical for every tenant'),
  ('compliance','regulatory_rule_version',    'Published rule versions; every tenant must be evaluated against the same one'),
  ('compliance','regulatory_rule_test',       'Test fixtures for published rules; contains no tenant data'),
  ('compliance','jurisdiction_holiday',       'Public holiday calendar for business-day deadlines'),
  ('app','managed_schema',                    'Meta-table describing the tenancy regime')
on conflict (schema_name, table_name) do nothing;

-- Protect every new tenant table. The registry above is what lets this see the
-- three new schemas at all.
do $$
declare v_count int;
begin
  v_count := app.apply_tenant_rls();
  raise notice 'app.apply_tenant_rls(): % tenant tables protected', v_count;
end
$$;

-- Reference data: read-only to the application role. Authoring regulatory
-- content runs as the owner through the admin path, never as mautodesk_app —
-- so a compromised application cannot rewrite the law it is evaluated against.
grant select on
  compliance.regulatory_jurisdiction,
  compliance.regulatory_authority,
  compliance.regulatory_source,
  compliance.regulatory_source_version,
  compliance.regulatory_rule,
  compliance.regulatory_rule_version,
  compliance.regulatory_rule_test,
  compliance.jurisdiction_holiday,
  app.managed_schema
  to mautodesk_app;

-- Tenant data: full DML, constrained by RLS.
do $$
declare r record;
begin
  for r in
    select t.table_schema, t.table_name
      from information_schema.tables t
     where t.table_type = 'BASE TABLE'
       and t.table_schema in ('compliance','lending','titling')
       and not exists (select 1 from app.rls_exempt_table x
                        where x.schema_name = t.table_schema and x.table_name = t.table_name)
  loop
    execute format('grant select, insert, update, delete on %I.%I to mautodesk_app',
                   r.table_schema, r.table_name);
  end loop;
end
$$;

-- Append-only tables: take back what the blanket grant just handed out. The
-- triggers already block mutation; a grant that says otherwise is a misleading
-- signal in an access review.
revoke update, delete on compliance.compliance_run     from mautodesk_app;
revoke update, delete on compliance.compliance_result  from mautodesk_app;
revoke update, delete on compliance.override_decision  from mautodesk_app;
revoke update, delete on lending.lender_program_version from mautodesk_app;
revoke update, delete on lending.lender_decision       from mautodesk_app;

grant usage, select on all sequences in schema compliance, lending, titling to mautodesk_app;

--------------------------------------------------------------------------------
-- Replace app.rls_coverage_gaps() so it reads the registry rather than a
-- hard-coded schema list. Behaviour is otherwise identical to V0006, including
-- the two structural exceptions for tables whose tenant key is not tenant_id.
--------------------------------------------------------------------------------
create or replace function app.rls_coverage_gaps()
returns table (schema_name text, table_name text, problem text)
language sql stable as $$
  with candidates as (
    select t.table_schema::text as s, t.table_name::text as n
      from information_schema.tables t
     where t.table_type = 'BASE TABLE'
       and t.table_schema in (select ms.schema_name from app.managed_schema ms)
       and not exists (select 1 from app.rls_exempt_table x
                        where x.schema_name = t.table_schema and x.table_name = t.table_name)
  )
  select c.s, c.n, 'RLS not enabled'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where not pc.relrowsecurity
  union all
  select c.s, c.n, 'RLS not FORCED (table owner would bypass it)'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where pc.relrowsecurity and not pc.relforcerowsecurity
  union all
  select c.s, c.n, 'RLS enabled but no policy defined'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where pc.relrowsecurity
     and not exists (select 1 from pg_policy p where p.polrelid = pc.oid)
  union all
  select c.s, c.n, 'no tenant_id column and not declared exempt'
    from candidates c
   where not exists (
     select 1 from information_schema.columns col
      where col.table_schema = c.s and col.table_name = c.n and col.column_name = 'tenant_id')
     and not (c.s = 'platform' and c.n in ('tenant','tenant_setting','retention_policy',
                                           'impersonation_session'))
     and not (c.s = 'identity' and c.n = 'role_permission');
$$;

comment on function app.rls_coverage_gaps() is
  'Returns zero rows in a healthy schema. CI fails the build on any row. The '
  'schema list comes from app.managed_schema — a schema that is not registered '
  'there is not checked, which is why registration is part of creating one. Do '
  'not "fix" a gap by adding the table to app.rls_exempt_table without a '
  'reviewed justification.';

--------------------------------------------------------------------------------
-- Permissions.
--
-- Catalogue rows only. Which role gets which permission for a given dealer is
-- the dealer's decision; the system roles below get the defaults their job
-- descriptions imply.
--------------------------------------------------------------------------------
insert into identity.permission (code, module, description, is_sensitive) values
  ('compliance.evaluation.read',  'compliance', 'View compliance results and Deal Guardian state', false),
  ('compliance.override.request', 'compliance', 'Request an override of a compliance rule', false),
  ('compliance.override.approve', 'compliance', 'Approve or reject an override request', true),
  ('compliance.rule.read',        'compliance', 'View regulatory rules, versions and sources', false),
  ('lending.lender.read',         'lending',    'View lenders and programs', false),
  ('lending.lender.write',        'lending',    'Configure lenders and programs', false),
  ('lending.program.approve',     'lending',    'Approve a lender program version for use', true),
  ('lending.submission.write',    'lending',    'Submit a deal to a lender and record decisions', false),
  ('lending.reserve.read',        'lending',    'View buy rate, sell rate and dealer reserve', true),
  ('titling.title.read',          'titling',    'View titles, holds and liens', false),
  ('titling.title.write',         'titling',    'Record and update titles, holds and liens', false),
  ('titling.tag.issue',           'titling',    'Assign and issue a temporary tag', false),
  ('titling.prereg.submit',       'titling',    'Submit vehicle pre-registration', false)
on conflict (code) do nothing;

-- Owner: everything, including what this migration just added. V0007 ran the
-- same statement against the catalogue as it stood then.
insert into identity.role_permission (role_id, permission_code)
select '11111111-0000-0000-0000-000000000001', code from identity.permission
on conflict do nothing;

insert into identity.role_permission (role_id, permission_code) values
  -- Sales Manager: sees readiness and lender options, approves neither.
  ('11111111-0000-0000-0000-000000000002','compliance.evaluation.read'),
  ('11111111-0000-0000-0000-000000000002','compliance.override.request'),
  ('11111111-0000-0000-0000-000000000002','compliance.override.approve'),
  ('11111111-0000-0000-0000-000000000002','compliance.rule.read'),
  ('11111111-0000-0000-0000-000000000002','lending.lender.read'),
  ('11111111-0000-0000-0000-000000000002','titling.title.read'),
  -- F&I Manager: owns lender structure and reserve; requests overrides, does
  -- not approve its own.
  ('11111111-0000-0000-0000-000000000004','compliance.evaluation.read'),
  ('11111111-0000-0000-0000-000000000004','compliance.override.request'),
  ('11111111-0000-0000-0000-000000000004','compliance.rule.read'),
  ('11111111-0000-0000-0000-000000000004','lending.lender.read'),
  ('11111111-0000-0000-0000-000000000004','lending.lender.write'),
  ('11111111-0000-0000-0000-000000000004','lending.submission.write'),
  ('11111111-0000-0000-0000-000000000004','lending.reserve.read'),
  ('11111111-0000-0000-0000-000000000004','titling.title.read'),
  -- Office / Title Clerk: the titling desk.
  ('11111111-0000-0000-0000-000000000005','compliance.evaluation.read'),
  ('11111111-0000-0000-0000-000000000005','compliance.rule.read'),
  ('11111111-0000-0000-0000-000000000005','titling.title.read'),
  ('11111111-0000-0000-0000-000000000005','titling.title.write'),
  ('11111111-0000-0000-0000-000000000005','titling.tag.issue'),
  ('11111111-0000-0000-0000-000000000005','titling.prereg.submit'),
  -- Read Only: readiness, nothing sensitive.
  ('11111111-0000-0000-0000-000000000007','compliance.evaluation.read'),
  ('11111111-0000-0000-0000-000000000007','titling.title.read')
on conflict do nothing;
