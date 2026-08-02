--------------------------------------------------------------------------------
-- MautoDesk — V0003 Inventory and CRM
--
-- The inventory module is the system's centre of gravity: the vehicle record is
-- referenced by deals, documents, photos, publishing, and reporting. Its shape
-- determines how much of the constitution's "enter data once" promise we can
-- actually keep.
--
-- Two decisions worth calling out:
--
--  1. VIN decode results are cached in a TENANT-INDEPENDENT table. A VIN decodes
--     the same for everyone, the response contains no customer data, and NHTSA
--     rate limits are real. `inventory.vin_decode_cache` therefore has no
--     tenant_id and no RLS — it is reference data. Vehicle-specific *overrides*
--     live on the tenant's vehicle row, so a dealer correcting a trim never
--     mutates shared data.
--
--  2. Cost data is separated from the vehicle row into `inventory.vehicle_cost`.
--     Most dealers hide acquisition and recon cost from salespeople; a separate
--     table makes "select the vehicle without the costs" the natural query
--     rather than something the API layer has to remember to strip.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Shared reference: VIN decode cache (no tenant, no RLS — see note 1 above)
--------------------------------------------------------------------------------
create table inventory.vin_decode_cache (
  vin            varchar(17) primary key,
  provider       text not null,              -- 'nhtsa_vpic' | 'dataone' | ...
  provider_version text,
  decoded_at     timestamptz not null default now(),
  raw_response   jsonb not null,
  model_year     int,
  make           text,
  model          text,
  trim           text,
  series         text,
  body_class     text,
  vehicle_type   text,
  drive_type     text,
  engine_cylinders int,
  engine_displacement_l numeric(5,2),
  engine_model   text,
  fuel_type      text,
  transmission_style text,
  transmission_speeds text,
  doors          int,
  gvwr           text,
  plant_country  text,
  manufacturer   text,
  error_code     text,
  error_text     text
);

create index vin_decode_cache_stale_ix on inventory.vin_decode_cache (decoded_at);

comment on table inventory.vin_decode_cache is
  'Deliberately not tenant-scoped: VIN decode output is public reference data '
  'containing no customer information. Do not add tenant_id or RLS here.';

--------------------------------------------------------------------------------
-- inventory.vehicle
--------------------------------------------------------------------------------
create table inventory.vehicle (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  stock_number        text not null,
  vin                 varchar(17),
  -- A vehicle may legitimately lack a VIN briefly (a trade appraised before the
  -- title is in hand), so VIN is nullable but uniquely constrained when present.

  -- Identity, seeded from the decode cache then dealer-editable. Storing these
  -- denormalized (rather than joining the cache) is deliberate: the decode may
  -- be corrected by the dealer, and a sold vehicle's description must not change
  -- because a provider updated its data three years later.
  model_year          int check (model_year between 1900 and 2100),
  make                text,
  model               text,
  trim                text,
  series              text,
  body_style          text,
  vehicle_type        text,
  drive_type          text,
  engine              text,
  engine_cylinders    int,
  engine_displacement_l numeric(5,2),
  fuel_type           text,
  transmission        text,
  doors               int,
  exterior_color      text,
  interior_color      text,
  mileage             int check (mileage >= 0),
  mileage_unit        text not null default 'mi' check (mileage_unit in ('mi','km')),
  odometer_status     text default 'actual'
                      check (odometer_status in ('actual','not_actual','exceeds_limits','exempt')),

  -- Condition and provenance
  condition           text not null default 'used'
                      check (condition in ('new','used','certified')),
  title_status        text default 'clean'
                      check (title_status in ('clean','salvage','rebuilt','flood','lemon','bonded','not_actual')),
  title_state         char(2),
  title_number        text,
  title_received_at   date,
  is_certified        boolean not null default false,
  has_accident_history boolean,
  owner_count         int,

  -- Merchandising
  description         text,
  ai_description_draft text,
  ai_description_approved_at timestamptz,
  ai_description_approved_by uuid,
  features            jsonb not null default '[]'::jsonb,  -- dealer-selected feature codes
  factory_options     jsonb not null default '[]'::jsonb,  -- from the decoder
  keywords            text[],

  -- Pricing (asking prices only; cost lives in inventory.vehicle_cost)
  list_price          numeric(14,2) check (list_price >= 0),
  msrp                numeric(14,2),
  internet_price      numeric(14,2),
  wholesale_price     numeric(14,2),
  price_visibility    text not null default 'show'
                      check (price_visibility in ('show','call','hide')),

  -- Lifecycle
  status              text not null default 'acquired'
                      check (status in ('acquired','in_recon','available','on_hold',
                                        'pending_sale','sold','delivered','wholesaled','archived')),
  acquired_at         date,
  acquisition_source  text,   -- auction | trade | private | dealer | consignment
  available_at        date,   -- front-line ready; drives days-to-front-line
  sold_at             date,
  location            text,
  lot_section         text,
  key_location        text,
  is_published        boolean not null default false,
  published_at        timestamptz,

  -- Turn time for a SOLD unit is fixed once it sells, so it is stored and
  -- indexable. Days-in-inventory for an UNSOLD unit depends on today's date and
  -- therefore cannot be a stored generated column (PostgreSQL requires an
  -- immutable expression); it is computed by inventory.v_vehicle_aging below,
  -- which is the only place that calculation is allowed to live.
  days_to_sale        int generated always as (
                        case when sold_at is not null and acquired_at is not null
                             then (sold_at - acquired_at) end) stored,

  search_vector       tsvector generated always as (
                        to_tsvector('english',
                          coalesce(stock_number,'') || ' ' ||
                          coalesce(vin,'') || ' ' ||
                          coalesce(model_year::text,'') || ' ' ||
                          coalesce(make,'') || ' ' ||
                          coalesce(model,'') || ' ' ||
                          coalesce(trim,'') || ' ' ||
                          coalesce(exterior_color,'') || ' ' ||
                          coalesce(description,''))) stored,

  notes               text,
  created_at          timestamptz not null default now(),
  created_by          uuid,
  updated_at          timestamptz not null default now(),
  updated_by          uuid,
  deleted_at          timestamptz,
  deleted_by          uuid
);

create unique index vehicle_tenant_stock_uq
  on inventory.vehicle (tenant_id, stock_number) where deleted_at is null;
create unique index vehicle_tenant_vin_uq
  on inventory.vehicle (tenant_id, vin) where vin is not null and deleted_at is null;

-- The inventory grid's default query: tenant + status, sorted by age.
create index vehicle_tenant_status_ix
  on inventory.vehicle (tenant_id, status, acquired_at desc) where deleted_at is null;
-- Faceted browse (year/make/model) on the grid and the public feed.
create index vehicle_tenant_ymm_ix
  on inventory.vehicle (tenant_id, make, model, model_year) where deleted_at is null;
-- Full-text search, tenant-partitioned via btree_gin so the GIN scan starts narrow.
create index vehicle_search_ix
  on inventory.vehicle using gin (tenant_id, search_vector);
-- Partial VIN / stock lookup ("last 6 of the VIN"), the single most common
-- lot-floor search. Trigram, not LIKE-prefix, because dealers type the middle.
create index vehicle_vin_trgm_ix
  on inventory.vehicle using gin (vin gin_trgm_ops) where deleted_at is null;
create index vehicle_stock_trgm_ix
  on inventory.vehicle using gin (stock_number gin_trgm_ops) where deleted_at is null;
-- Public feed: published + available only.
create index vehicle_published_ix
  on inventory.vehicle (tenant_id, published_at desc)
  where is_published and status = 'available' and deleted_at is null;

-- The single definition of vehicle aging. Reports, the grid, and the dashboard
-- all read this view so they cannot disagree about what "60 days old" means.
create view inventory.v_vehicle_aging as
select v.id,
       v.tenant_id,
       v.stock_number,
       v.status,
       v.acquired_at,
       v.available_at,
       v.sold_at,
       v.days_to_sale,
       case when v.sold_at is null and v.acquired_at is not null
            then (current_date - v.acquired_at) end            as days_in_inventory,
       case when v.available_at is not null and v.acquired_at is not null
            then (v.available_at - v.acquired_at) end          as days_to_front_line
  from inventory.vehicle v
 where v.deleted_at is null;

create trigger vehicle_set_updated_at before update on inventory.vehicle
  for each row execute function app.set_updated_at();
create trigger vehicle_enforce_tenant before insert or update on inventory.vehicle
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Costs — separated so "the salesperson view" is a different table, not a
-- different projection someone has to remember to apply.
--------------------------------------------------------------------------------
create table inventory.vehicle_cost (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid not null references platform.tenant(id),
  vehicle_id    uuid not null references inventory.vehicle(id) on delete cascade,
  category      text not null
                check (category in ('acquisition','transport','recon','parts','labor',
                                    'inspection','title_fee','auction_fee','pack','floorplan',
                                    'advertising','other')),
  description   text,
  amount        numeric(14,2) not null check (amount >= 0),
  vendor_name   text,
  invoice_number text,
  incurred_on   date not null default current_date,
  is_billable_to_customer boolean not null default false,
  approved_at   timestamptz,
  approved_by   uuid,
  created_at    timestamptz not null default now(),
  created_by    uuid,
  updated_at    timestamptz not null default now(),
  updated_by    uuid,
  deleted_at    timestamptz,
  deleted_by    uuid
);

create index vehicle_cost_vehicle_ix
  on inventory.vehicle_cost (tenant_id, vehicle_id) where deleted_at is null;
create index vehicle_cost_category_ix
  on inventory.vehicle_cost (tenant_id, category, incurred_on) where deleted_at is null;

create trigger vehicle_cost_set_updated_at before update on inventory.vehicle_cost
  for each row execute function app.set_updated_at();

--------------------------------------------------------------------------------
-- Photos. The `object_key` points at Cloudflare R2; `sha256` is recorded at
-- promotion time (ADR-0005) and is what proves an object was not swapped.
--------------------------------------------------------------------------------
create table inventory.vehicle_photo (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  vehicle_id     uuid not null references inventory.vehicle(id) on delete cascade,
  object_key     text not null,
  thumbnail_key  text,
  large_key      text,
  original_key   text,          -- retained pre-processing original
  content_type   text not null,
  byte_size      bigint not null,
  width          int,
  height         int,
  sha256         bytea not null,
  sort_order     int not null default 0,
  caption        text,
  is_primary     boolean not null default false,
  processing_status text not null default 'pending'
                 check (processing_status in ('pending','scanning','processing','ready','rejected')),
  rejection_reason text,
  has_background_removed boolean not null default false,
  has_watermark  boolean not null default false,
  ai_enhanced    boolean not null default false,
  exif_stripped  boolean not null default false,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now(),
  deleted_at     timestamptz,
  deleted_by     uuid
);

create index vehicle_photo_vehicle_ix
  on inventory.vehicle_photo (tenant_id, vehicle_id, sort_order) where deleted_at is null;
create unique index vehicle_photo_primary_uq
  on inventory.vehicle_photo (vehicle_id) where is_primary and deleted_at is null;

--------------------------------------------------------------------------------
-- Status and price history. Both feed reporting (turn time, price-drop
-- effectiveness) and the vehicle timeline the constitution asks for.
--------------------------------------------------------------------------------
create table inventory.vehicle_status_history (
  id           bigint generated always as identity primary key,
  tenant_id    uuid not null references platform.tenant(id),
  vehicle_id   uuid not null references inventory.vehicle(id) on delete cascade,
  from_status  text,
  to_status    text not null,
  reason       text,
  changed_at   timestamptz not null default now(),
  changed_by   uuid
);

create index vehicle_status_history_ix
  on inventory.vehicle_status_history (tenant_id, vehicle_id, changed_at desc);

create table inventory.vehicle_price_history (
  id             bigint generated always as identity primary key,
  tenant_id      uuid not null references platform.tenant(id),
  vehicle_id     uuid not null references inventory.vehicle(id) on delete cascade,
  price_type     text not null check (price_type in ('list','internet','wholesale')),
  old_price      numeric(14,2),
  new_price      numeric(14,2) not null,
  reason         text,
  source         text not null default 'user' check (source in ('user','ai_suggestion','rule')),
  changed_at     timestamptz not null default now(),
  changed_by     uuid
);

create index vehicle_price_history_ix
  on inventory.vehicle_price_history (tenant_id, vehicle_id, changed_at desc);

--------------------------------------------------------------------------------
-- Recon workflow — the step data that makes "days to front line" measurable.
--------------------------------------------------------------------------------
create table inventory.recon_step (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  vehicle_id   uuid not null references inventory.vehicle(id) on delete cascade,
  name         text not null,
  sequence     int not null default 0,
  status       text not null default 'pending'
               check (status in ('pending','in_progress','blocked','complete','skipped')),
  assigned_to  uuid references identity."user"(id),
  vendor_name  text,
  estimated_cost numeric(14,2),
  started_at   timestamptz,
  completed_at timestamptz,
  notes        text,
  created_at   timestamptz not null default now(),
  created_by   uuid,
  updated_at   timestamptz not null default now(),
  updated_by   uuid
);

create index recon_step_vehicle_ix on inventory.recon_step (tenant_id, vehicle_id, sequence);
create index recon_step_open_ix
  on inventory.recon_step (tenant_id, status, assigned_to)
  where status in ('pending','in_progress','blocked');

--------------------------------------------------------------------------------
-- CRM
--
-- `customer` holds identity and PII. Sensitive identifiers are envelope-
-- encrypted (ADR-0007) with a searchable blind index (HMAC of the normalized
-- value under a tenant-scoped key) so "find by SSN" works without a plaintext
-- column. The blind index is NOT reversible and is not a substitute for the
-- ciphertext.
--------------------------------------------------------------------------------
create table crm.customer (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  customer_number    text,
  type               text not null default 'individual'
                     check (type in ('individual','business')),
  first_name         text,
  middle_name        text,
  last_name          text,
  business_name      text,
  display_name       text generated always as (
                       coalesce(nullif(business_name, ''),
                                nullif(trim(coalesce(first_name,'') || ' ' || coalesce(last_name,'')), ''))
                     ) stored,
  email              citext,
  phone_mobile       text,
  phone_home         text,
  phone_work         text,
  preferred_contact  text check (preferred_contact in ('email','sms','phone','mail')),

  address_line1      text,
  address_line2      text,
  city               text,
  county             text,
  state_code         char(2),
  postal_code        text,
  country_code       char(2) not null default 'US',
  -- Resolved taxing jurisdiction for the deal engine. Kansas is destination-
  -- based, so this cannot be derived from state alone (see docs/02 §9).
  tax_jurisdiction_id uuid,

  date_of_birth      date,
  -- Encrypted identifiers. Never add a plaintext column for any of these.
  ssn_enc            bytea,
  ssn_kid            text,
  ssn_blind_index    bytea,
  ssn_last4          char(4),          -- display only; safe to show, safe to log? No — masked in UI
  dl_number_enc      bytea,
  dl_number_kid      text,
  dl_number_blind_index bytea,
  dl_state           char(2),
  dl_expires_on      date,

  employer_name      text,
  employment_years   numeric(4,1),
  monthly_income     numeric(14,2),
  housing_status     text,
  monthly_housing_payment numeric(14,2),

  -- Consent tracking (GDPR/CCPA readiness + TCPA reality for SMS).
  consent_email      boolean not null default false,
  consent_sms        boolean not null default false,
  consent_call       boolean not null default false,
  consent_recorded_at timestamptz,
  consent_source     text,
  do_not_contact     boolean not null default false,

  tags               text[] not null default '{}',
  notes              text,
  search_vector      tsvector generated always as (
                       to_tsvector('english',
                         coalesce(first_name,'') || ' ' || coalesce(last_name,'') || ' ' ||
                         coalesce(business_name,'') || ' ' || coalesce(email::text,'') || ' ' ||
                         coalesce(city,'') || ' ' || coalesce(postal_code,''))) stored,

  created_at         timestamptz not null default now(),
  created_by         uuid,
  updated_at         timestamptz not null default now(),
  updated_by         uuid,
  deleted_at         timestamptz,
  deleted_by         uuid,
  -- Real erasure, distinct from soft delete. Set when the encryption keys for
  -- this record are destroyed (crypto-shred) in response to a deletion request.
  erased_at          timestamptz
);

create unique index customer_tenant_number_uq
  on crm.customer (tenant_id, customer_number)
  where customer_number is not null and deleted_at is null;
create index customer_tenant_name_ix
  on crm.customer (tenant_id, last_name, first_name) where deleted_at is null;
create index customer_email_ix on crm.customer (tenant_id, email) where deleted_at is null;
create index customer_phone_trgm_ix
  on crm.customer using gin ((coalesce(phone_mobile,'') || ' ' ||
                              coalesce(phone_home,'')  || ' ' ||
                              coalesce(phone_work,'')) gin_trgm_ops);
create index customer_search_ix on crm.customer using gin (tenant_id, search_vector);
create index customer_ssn_bi_ix on crm.customer (tenant_id, ssn_blind_index)
  where ssn_blind_index is not null;

create trigger customer_set_updated_at before update on crm.customer
  for each row execute function app.set_updated_at();
create trigger customer_enforce_tenant before insert or update on crm.customer
  for each row execute function app.enforce_tenant();

comment on column crm.customer.ssn_last4 is
  'Display convenience only. Must still be masked in UI by default and is '
  'covered by the log redaction policy — it is PII, not metadata.';

--------------------------------------------------------------------------------
-- Leads
--------------------------------------------------------------------------------
create table crm.lead (
  id              uuid primary key default gen_random_uuid(),
  tenant_id       uuid not null references platform.tenant(id),
  customer_id     uuid references crm.customer(id),
  vehicle_id      uuid references inventory.vehicle(id),
  source          text not null,        -- website | facebook | cars_com | walk_in | phone | referral
  source_detail   text,
  campaign        text,
  status          text not null default 'new'
                  check (status in ('new','contacted','working','appointment_set','shown',
                                    'negotiating','sold','lost','duplicate')),
  lost_reason     text,
  priority        text not null default 'normal' check (priority in ('low','normal','high')),
  score           int check (score between 0 and 100),
  score_reason    text,
  ai_summary      text,
  assigned_to     uuid references identity."user"(id),
  assigned_at     timestamptz,
  first_response_at timestamptz,
  last_activity_at  timestamptz,
  message         text,
  trade_vehicle_description text,
  -- Response time is the single strongest predictor of lead conversion, so it
  -- is a stored generated column, not something each report recomputes.
  response_minutes int generated always as (
                     case when first_response_at is not null
                          then (extract(epoch from (first_response_at - created_at)) / 60)::int
                     end) stored,
  created_at      timestamptz not null default now(),
  created_by      uuid,
  updated_at      timestamptz not null default now(),
  updated_by      uuid,
  deleted_at      timestamptz,
  deleted_by      uuid
);

create index lead_tenant_status_ix
  on crm.lead (tenant_id, status, created_at desc) where deleted_at is null;
create index lead_assigned_ix
  on crm.lead (tenant_id, assigned_to, status) where deleted_at is null;
create index lead_customer_ix on crm.lead (tenant_id, customer_id);
create index lead_vehicle_ix  on crm.lead (tenant_id, vehicle_id);
-- Unresponded leads: the queue that actually drives the sales floor.
create index lead_unresponded_ix
  on crm.lead (tenant_id, created_at)
  where first_response_at is null and status = 'new' and deleted_at is null;

create trigger lead_set_updated_at before update on crm.lead
  for each row execute function app.set_updated_at();
create trigger lead_enforce_tenant before insert or update on crm.lead
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- Tasks and appointments
--------------------------------------------------------------------------------
create table crm.task (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid not null references platform.tenant(id),
  title         text not null,
  description   text,
  type          text not null default 'todo'
                check (type in ('todo','call','email','sms','follow_up','appointment','recon','title')),
  status        text not null default 'open'
                check (status in ('open','in_progress','completed','cancelled')),
  priority      text not null default 'normal' check (priority in ('low','normal','high','urgent')),
  due_at        timestamptz,
  completed_at  timestamptz,
  assigned_to   uuid references identity."user"(id),
  customer_id   uuid references crm.customer(id),
  lead_id       uuid references crm.lead(id),
  vehicle_id    uuid references inventory.vehicle(id),
  deal_id       uuid,                          -- FK added in V0004
  created_at    timestamptz not null default now(),
  created_by    uuid,
  updated_at    timestamptz not null default now(),
  updated_by    uuid,
  deleted_at    timestamptz,
  deleted_by    uuid
);

create index task_assigned_open_ix
  on crm.task (tenant_id, assigned_to, due_at)
  where status in ('open','in_progress') and deleted_at is null;
create index task_customer_ix on crm.task (tenant_id, customer_id) where deleted_at is null;

create table crm.appointment (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid not null references platform.tenant(id),
  customer_id   uuid references crm.customer(id),
  lead_id       uuid references crm.lead(id),
  vehicle_id    uuid references inventory.vehicle(id),
  assigned_to   uuid references identity."user"(id),
  type          text not null default 'test_drive'
                check (type in ('test_drive','appraisal','delivery','service','signing','other')),
  status        text not null default 'scheduled'
                check (status in ('scheduled','confirmed','arrived','no_show','completed','cancelled')),
  starts_at     timestamptz not null,
  ends_at       timestamptz not null,
  location      text,
  notes         text,
  reminder_sent_at timestamptz,
  external_calendar_id text,
  created_at    timestamptz not null default now(),
  created_by    uuid,
  updated_at    timestamptz not null default now(),
  updated_by    uuid,
  deleted_at    timestamptz,
  deleted_by    uuid,
  constraint appointment_time_ck check (ends_at > starts_at)
);

create index appointment_calendar_ix
  on crm.appointment (tenant_id, starts_at) where deleted_at is null;
create index appointment_user_ix
  on crm.appointment (tenant_id, assigned_to, starts_at) where deleted_at is null;

--------------------------------------------------------------------------------
-- crm.activity — the unified timeline. Every module writes here via a domain
-- event handler, which is what makes the customer and vehicle timelines the
-- constitution asks for possible without each module querying every other one.
--------------------------------------------------------------------------------
create table crm.activity (
  id           bigint generated always as identity primary key,
  tenant_id    uuid not null references platform.tenant(id),
  occurred_at  timestamptz not null default now(),
  type         text not null,     -- note | call | email | sms | status_change | price_change
                                  -- | photo_added | document_signed | appointment | deal_stage
  summary      text not null,
  body         text,
  customer_id  uuid references crm.customer(id),
  lead_id      uuid references crm.lead(id),
  vehicle_id   uuid references inventory.vehicle(id),
  deal_id      uuid,                          -- FK added in V0004
  actor_id     uuid,
  actor_display text,
  metadata     jsonb not null default '{}'::jsonb,
  is_pinned    boolean not null default false
);

create index activity_customer_ix on crm.activity (tenant_id, customer_id, occurred_at desc);
create index activity_vehicle_ix  on crm.activity (tenant_id, vehicle_id, occurred_at desc);
create index activity_lead_ix     on crm.activity (tenant_id, lead_id, occurred_at desc);
create index activity_deal_ix     on crm.activity (tenant_id, deal_id, occurred_at desc);
