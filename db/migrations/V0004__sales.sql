--------------------------------------------------------------------------------
-- MautoDesk — V0004 Sales, jurisdictions, and the deal calculation ledger
--
-- This is the highest-risk schema in the system. A wrong number here is a wrong
-- number on a signed retail contract.
--
-- Three structural commitments, all from ADR-0008:
--
--  1. Tax and fee rules are DATA, versioned with effective dates and keyed to a
--     TAXING JURISDICTION, not a state. Kansas assesses local rates that vary
--     by destination address; a state-keyed design would need a schema change
--     within months of launching there.
--
--  2. Every deal calculation is SNAPSHOTTED immutably: inputs, the rule-set
--     version used, and the full computed breakdown. Recomputing a 2026 deal
--     with 2029 rules is a defect. The snapshot is what an auditor reads.
--
--  3. Money is numeric(14,2); rates are numeric(9,6). No float, anywhere, ever.
--     An architecture test fails the build if a double reaches Sales.Domain.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Taxing jurisdictions
--
-- Reference data, shared across tenants (a Kansas county rate is not a dealer's
-- private information), so no tenant_id and no RLS. Sourced and versioned.
--------------------------------------------------------------------------------
create table sales.tax_jurisdiction (
  id            uuid primary key default gen_random_uuid(),
  state_code    char(2) not null,
  level         text not null check (level in ('state','county','city','special')),
  name          text not null,
  fips_code     text,
  parent_id     uuid references sales.tax_jurisdiction(id),
  is_active     boolean not null default true,
  created_at    timestamptz not null default now()
);

create unique index tax_jurisdiction_uq
  on sales.tax_jurisdiction (state_code, level, name, coalesce(fips_code, ''));
create index tax_jurisdiction_state_ix on sales.tax_jurisdiction (state_code) where is_active;

-- Postal code → jurisdiction resolution. Deliberately many-to-many: US ZIPs
-- cross county lines, so a ZIP can map to several jurisdictions and the deal
-- flow must disambiguate (by county selection) rather than silently guess.
create table sales.postal_jurisdiction (
  postal_code     text not null,
  jurisdiction_id uuid not null references sales.tax_jurisdiction(id),
  county_name     text,
  city_name       text,
  is_primary      boolean not null default false,
  primary key (postal_code, jurisdiction_id)
);

create index postal_jurisdiction_zip_ix on sales.postal_jurisdiction (postal_code);

--------------------------------------------------------------------------------
-- Rule sets
--
-- `rules` is jsonb, deliberately. The shape of a jurisdiction's rules varies
-- (trade-in credit treatment, caps, tiered rates, fee schedules) and modelling
-- every variation relationally would produce a schema nobody can read. The
-- jsonb is validated against a versioned JSON Schema in the application, and
-- the DealEngine deserializes it into a strongly-typed RuleSet — so it is typed
-- where it matters (in the calculation) and flexible where it must be (storage).
--
-- Every row must cite its source and carry a human approval. A rule set without
-- `approved_at` is never used by the engine.
--------------------------------------------------------------------------------
create table sales.rule_set (
  id               uuid primary key default gen_random_uuid(),
  jurisdiction_id  uuid not null references sales.tax_jurisdiction(id),
  schema_version   int not null default 1,
  version          int not null,
  effective_from   date not null,
  effective_to     date,
  rules            jsonb not null,
  source_citation  text not null,      -- statute / DMV publication / bulletin reference
  source_url       text,
  notes            text,
  approved_at      timestamptz,
  approved_by      text,               -- named reviewer (CPA / compliance counsel)
  created_at       timestamptz not null default now(),
  constraint rule_set_period_ck check (effective_to is null or effective_to > effective_from)
);

create unique index rule_set_version_uq on sales.rule_set (jurisdiction_id, version);
create index rule_set_effective_ix
  on sales.rule_set (jurisdiction_id, effective_from desc) where approved_at is not null;

-- No two approved rule sets may overlap in time for the same jurisdiction.
-- Ambiguity here means the engine could pick either one for a given deal date.
create extension if not exists btree_gist;
alter table sales.rule_set add constraint rule_set_no_overlap
  exclude using gist (
    jurisdiction_id with =,
    daterange(effective_from, coalesce(effective_to, 'infinity'::date), '[)') with &&
  ) where (approved_at is not null);

comment on table sales.rule_set is
  'Tax and fee rules as versioned, effective-dated, source-cited data. An '
  'unapproved row (approved_at is null) is invisible to the deal engine. '
  'Launch jurisdictions: Oklahoma, Kansas, Texas.';

-- Dealer-configurable fees (doc fee, dealer prep) with statutory caps carried
-- from the rule set so the UI can warn before the contract is printed.
create table sales.fee_definition (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  code           text not null,
  name           text not null,
  category       text not null
                 check (category in ('doc_fee','dealer_prep','title','registration','lien',
                                     'inspection','temp_tag','smog','electronic_filing','other')),
  default_amount numeric(14,2) not null default 0,
  is_taxable     boolean not null default false,
  is_state_fee   boolean not null default false,   -- passed through, not dealer revenue
  applies_to     text not null default 'retail'
                 check (applies_to in ('retail','wholesale','both')),
  is_active      boolean not null default true,
  sort_order     int not null default 0,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now(),
  updated_by     uuid
);

create unique index fee_definition_tenant_code_uq
  on sales.fee_definition (tenant_id, code) where is_active;

--------------------------------------------------------------------------------
-- Deals
--
-- `deal` is the aggregate root and holds workflow state. All money lives in the
-- calculation snapshot (sales.deal_calculation), NOT on this row. Denormalizing
-- totals here would create two sources of truth for the number on the contract.
--------------------------------------------------------------------------------
create table sales.deal (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  deal_number        text not null,
  type               text not null default 'retail'
                     check (type in ('retail','wholesale','lease','consignment')),
  status             text not null default 'quote'
                     check (status in ('quote','pending','approved','contracted','funded',
                                       'delivered','completed','cancelled','unwound')),
  vehicle_id         uuid references inventory.vehicle(id),
  customer_id        uuid references crm.customer(id),
  co_buyer_id        uuid references crm.customer(id),
  lead_id            uuid references crm.lead(id),
  salesperson_id     uuid references identity."user"(id),
  sales_manager_id   uuid references identity."user"(id),
  finance_manager_id uuid references identity."user"(id),

  -- Which jurisdiction's rules govern this deal, resolved at quote time and
  -- frozen. Changing the customer's address after contracting requires an
  -- explicit recalculation, not a silent re-resolve.
  jurisdiction_id    uuid references sales.tax_jurisdiction(id),

  -- Pointer to the currently authoritative calculation snapshot.
  current_calculation_id uuid,

  quoted_at          timestamptz,
  approved_at        timestamptz,
  contracted_at      timestamptz,
  funded_at          timestamptz,
  delivered_at       timestamptz,
  completed_at       timestamptz,
  cancelled_at       timestamptz,
  cancel_reason      text,

  delivery_method    text check (delivery_method in ('pickup','delivery','shipped')),
  promised_delivery_at timestamptz,
  notes              text,
  internal_notes     text,

  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now(),
  updated_by         uuid,
  deleted_at         timestamptz,
  deleted_by         uuid
);

create unique index deal_tenant_number_uq
  on sales.deal (tenant_id, deal_number) where deleted_at is null;
create index deal_tenant_status_ix
  on sales.deal (tenant_id, status, created_at desc) where deleted_at is null;
create index deal_vehicle_ix     on sales.deal (tenant_id, vehicle_id) where deleted_at is null;
create index deal_customer_ix    on sales.deal (tenant_id, customer_id) where deleted_at is null;
create index deal_salesperson_ix on sales.deal (tenant_id, salesperson_id, status)
  where deleted_at is null;
-- Pipeline board: open deals only.
create index deal_open_ix
  on sales.deal (tenant_id, status, updated_at desc)
  where status in ('quote','pending','approved','contracted') and deleted_at is null;

create trigger deal_set_updated_at before update on sales.deal
  for each row execute function app.set_updated_at();
create trigger deal_enforce_tenant before insert or update on sales.deal
  for each row execute function app.enforce_tenant();

-- Deferred FKs from V0003 now that sales.deal exists.
alter table crm.task     add constraint task_deal_fk
  foreign key (deal_id) references sales.deal(id);
alter table crm.activity add constraint activity_deal_fk
  foreign key (deal_id) references sales.deal(id);

--------------------------------------------------------------------------------
-- Trade-ins
--------------------------------------------------------------------------------
create table sales.trade_in (
  id               uuid primary key default gen_random_uuid(),
  tenant_id        uuid not null references platform.tenant(id),
  deal_id          uuid not null references sales.deal(id) on delete cascade,
  vin              varchar(17),
  model_year       int,
  make             text,
  model            text,
  trim             text,
  mileage          int check (mileage >= 0),
  exterior_color   text,
  condition_notes  text,
  allowance        numeric(14,2) not null default 0 check (allowance >= 0),
  actual_cash_value numeric(14,2) check (actual_cash_value >= 0),
  payoff_amount    numeric(14,2) not null default 0 check (payoff_amount >= 0),
  payoff_good_through date,
  lienholder_name  text,
  lienholder_account text,
  title_in_hand    boolean not null default false,
  title_status     text,
  -- Set when the trade is booked into inventory, closing the loop from sale
  -- back into acquisition without re-entering the vehicle.
  received_vehicle_id uuid references inventory.vehicle(id),
  created_at       timestamptz not null default now(),
  created_by       uuid,
  updated_at       timestamptz not null default now(),
  updated_by       uuid,
  deleted_at       timestamptz
);

create index trade_in_deal_ix on sales.trade_in (tenant_id, deal_id) where deleted_at is null;

--------------------------------------------------------------------------------
-- Deal line items: fees, add-ons, aftermarket products, rebates, discounts.
-- One row per line so the contract can be reproduced exactly.
--------------------------------------------------------------------------------
create table sales.deal_line_item (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  deal_id        uuid not null references sales.deal(id) on delete cascade,
  kind           text not null
                 check (kind in ('fee','product','accessory','discount','rebate','service_contract',
                                 'gap','warranty','tax_override')),
  fee_definition_id uuid references sales.fee_definition(id),
  code           text,
  description    text not null,
  amount         numeric(14,2) not null,
  cost           numeric(14,2),          -- dealer cost, for gross profit
  is_taxable     boolean not null default false,
  is_state_fee   boolean not null default false,
  provider_name  text,
  sort_order     int not null default 0,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now(),
  deleted_at     timestamptz
);

create index deal_line_item_deal_ix
  on sales.deal_line_item (tenant_id, deal_id, sort_order) where deleted_at is null;

--------------------------------------------------------------------------------
-- Financing
--------------------------------------------------------------------------------
create table sales.finance_terms (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  deal_id            uuid not null references sales.deal(id) on delete cascade,
  method             text not null
                     check (method in ('cash','outside_finance','dealer_finance','bhph','lease')),
  lender_name        text,
  lender_id          text,
  approval_number    text,
  approved_at        timestamptz,
  amount_financed    numeric(14,2),
  down_payment       numeric(14,2) not null default 0,
  apr                numeric(9,6),
  term_months        int check (term_months > 0),
  payment_amount     numeric(14,2),
  payment_frequency  text default 'monthly'
                     check (payment_frequency in ('weekly','biweekly','semimonthly','monthly')),
  first_payment_on   date,
  finance_charge     numeric(14,2),
  total_of_payments  numeric(14,2),
  buy_rate           numeric(9,6),      -- lender's rate
  sell_rate          numeric(9,6),      -- rate presented to the customer
  reserve_amount     numeric(14,2),     -- dealer participation
  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now(),
  deleted_at         timestamptz
);

create unique index finance_terms_deal_uq
  on sales.finance_terms (deal_id) where deleted_at is null;

comment on column sales.finance_terms.sell_rate is
  'Rate participation is regulated and disclosure-sensitive. Any UI exposing '
  'buy_rate vs sell_rate is gated behind sales.deal.finance.read and audited.';

--------------------------------------------------------------------------------
-- Deposits and payments received against a deal.
-- v1 RECORDS payments; it does not process them (Phase 1 §8).
--------------------------------------------------------------------------------
create table sales.deal_payment (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  deal_id        uuid not null references sales.deal(id) on delete cascade,
  kind           text not null check (kind in ('deposit','down_payment','payoff','refund','other')),
  amount         numeric(14,2) not null,
  method         text check (method in ('cash','check','card','ach','wire','financed','other')),
  reference      text,
  received_at    timestamptz not null default now(),
  received_by    uuid,
  is_refunded    boolean not null default false,
  refunded_at    timestamptz,
  notes          text,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  deleted_at     timestamptz
);

create index deal_payment_deal_ix
  on sales.deal_payment (tenant_id, deal_id, received_at) where deleted_at is null;

--------------------------------------------------------------------------------
-- sales.deal_calculation — the immutable snapshot (ADR-0008).
--
-- Append-only: a change produces a new version. `input` and `output` are the
-- exact serialized DealInput and DealCalculation the pure engine saw and
-- returned, so a dispute can be replayed byte-for-byte against the pinned
-- engine version.
--------------------------------------------------------------------------------
create table sales.deal_calculation (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references platform.tenant(id),
  deal_id           uuid not null references sales.deal(id) on delete cascade,
  version           int not null,
  rule_set_id       uuid references sales.rule_set(id),
  rule_set_version  int,
  engine_version    text not null,        -- semver of MautoDesk.Sales.DealEngine
  calculated_at     timestamptz not null default now(),
  calculated_by     uuid,
  input             jsonb not null,
  output            jsonb not null,

  -- Extracted from `output` for indexing and reporting. These are a projection
  -- of the snapshot, never an independent source of truth.
  selling_price     numeric(14,2) not null,
  trade_allowance   numeric(14,2) not null default 0,
  trade_payoff      numeric(14,2) not null default 0,
  taxable_amount    numeric(14,2) not null default 0,
  sales_tax         numeric(14,2) not null default 0,
  total_fees        numeric(14,2) not null default 0,
  total_state_fees  numeric(14,2) not null default 0,
  rebates           numeric(14,2) not null default 0,
  cash_down         numeric(14,2) not null default 0,
  amount_financed   numeric(14,2) not null default 0,
  total_sale_price  numeric(14,2) not null,
  balance_due       numeric(14,2) not null,

  -- Gross profit components, computed against inventory.vehicle_cost at
  -- snapshot time so a later cost correction does not silently restate a
  -- reported commission.
  vehicle_cost      numeric(14,2),
  front_gross       numeric(14,2),
  back_gross        numeric(14,2),
  total_gross       numeric(14,2),

  content_hash      bytea not null,       -- sha256 of input||output; printed on the contract
  superseded_at     timestamptz,
  superseded_reason text
);

create unique index deal_calculation_version_uq on sales.deal_calculation (deal_id, version);
create index deal_calculation_deal_ix on sales.deal_calculation (tenant_id, deal_id, version desc);
create index deal_calculation_current_ix
  on sales.deal_calculation (tenant_id, calculated_at desc) where superseded_at is null;

create trigger deal_calculation_immutable before update or delete on sales.deal_calculation
  for each row execute function app.deny_mutation();

alter table sales.deal
  add constraint deal_current_calculation_fk
  foreign key (current_calculation_id) references sales.deal_calculation(id);

comment on table sales.deal_calculation is
  'Append-only. A correction is a new version with the prior row marked '
  'superseded. UPDATE and DELETE are blocked by trigger — if you need to change '
  'a number, you need a new snapshot, and the audit trail should show both.';

--------------------------------------------------------------------------------
-- Commissions — computed from a snapshot, so a plan change does not restate
-- already-paid commission.
--------------------------------------------------------------------------------
create table sales.commission_plan (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  name         text not null,
  is_active    boolean not null default true,
  effective_from date not null default current_date,
  effective_to   date,
  rules        jsonb not null,     -- tiers, flats, pack, minimums; typed in the engine
  created_at   timestamptz not null default now(),
  created_by   uuid,
  updated_at   timestamptz not null default now()
);

create table sales.commission (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  deal_id            uuid not null references sales.deal(id) on delete cascade,
  user_id            uuid not null references identity."user"(id),
  plan_id            uuid references sales.commission_plan(id),
  calculation_id     uuid references sales.deal_calculation(id),
  role               text not null check (role in ('salesperson','sales_manager','finance_manager')),
  basis              numeric(14,2),
  amount             numeric(14,2) not null,
  computed_at        timestamptz not null default now(),
  approved_at        timestamptz,
  approved_by        uuid,
  paid_at            timestamptz,
  pay_period         date,
  notes              text
);

create index commission_user_period_ix on sales.commission (tenant_id, user_id, pay_period);
create index commission_deal_ix on sales.commission (tenant_id, deal_id);
