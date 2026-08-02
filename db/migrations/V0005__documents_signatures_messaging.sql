--------------------------------------------------------------------------------
-- MautoDesk — V0005 Documents, OCR, e-signature, messaging, publishing
--
-- The e-signature tables are shaped by ADR-0009: the signature image is the
-- least important thing we store. What makes a signed contract defensible is
-- the evidence package — consent, attribution, intent, integrity, association,
-- environment, and retention — so each of those has an explicit home here
-- rather than being buried in a metadata blob.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- documents.document — the file record. One row per logical document; content
-- lives in versions so nothing is ever overwritten.
--------------------------------------------------------------------------------
create table documents.document (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  category       text not null
                 check (category in ('drivers_license','insurance','title','registration',
                                     'contract','buyers_order','bill_of_sale','odometer',
                                     'inspection','invoice','purchase_agreement','finance',
                                     'trade_document','payoff','photo','other')),
  name           text not null,
  description    text,
  -- Polymorphic attachment. Exactly one owner is required; enforced below.
  vehicle_id     uuid references inventory.vehicle(id),
  customer_id    uuid references crm.customer(id),
  deal_id        uuid references sales.deal(id),
  lead_id        uuid references crm.lead(id),

  current_version_id uuid,
  version_count  int not null default 0,
  status         text not null default 'active'
                 check (status in ('active','superseded','void','archived')),
  is_required    boolean not null default false,   -- drives deal-jacket completeness
  expires_on     date,                             -- insurance, DL, payoff quotes
  -- Retention: set from the tenant's policy at creation; a legal hold blocks purge.
  retain_until   date,
  legal_hold     boolean not null default false,

  search_vector  tsvector generated always as (
                   to_tsvector('english', coalesce(name,'') || ' ' || coalesce(description,''))
                 ) stored,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now(),
  updated_by     uuid,
  deleted_at     timestamptz,
  deleted_by     uuid,

  constraint document_owner_ck check (
    num_nonnulls(vehicle_id, customer_id, deal_id, lead_id) >= 1
  )
);

create index document_deal_ix     on documents.document (tenant_id, deal_id) where deleted_at is null;
create index document_vehicle_ix  on documents.document (tenant_id, vehicle_id) where deleted_at is null;
create index document_customer_ix on documents.document (tenant_id, customer_id) where deleted_at is null;
create index document_category_ix on documents.document (tenant_id, category, created_at desc)
  where deleted_at is null;
create index document_search_ix   on documents.document using gin (tenant_id, search_vector);
create index document_expiring_ix on documents.document (tenant_id, expires_on)
  where expires_on is not null and deleted_at is null;
create index document_purgeable_ix on documents.document (retain_until)
  where retain_until is not null and not legal_hold;

create trigger document_set_updated_at before update on documents.document
  for each row execute function app.set_updated_at();
create trigger document_enforce_tenant before insert or update on documents.document
  for each row execute function app.enforce_tenant();

--------------------------------------------------------------------------------
-- documents.document_version — append-only content. `sha256` is recorded at
-- promotion out of quarantine (ADR-0005) and is what proves the stored object
-- is the object we scanned.
--------------------------------------------------------------------------------
create table documents.document_version (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  document_id    uuid not null references documents.document(id) on delete cascade,
  version        int not null,
  bucket         text not null,
  object_key     text not null,
  content_type   text not null,
  byte_size      bigint not null check (byte_size > 0),
  sha256         bytea not null,
  page_count     int,
  scan_status    text not null default 'pending'
                 check (scan_status in ('pending','clean','infected','error','skipped')),
  scan_engine    text,
  scanned_at     timestamptz,
  is_encrypted   boolean not null default false,
  encryption_key_id uuid references app.encryption_key(id),
  source         text not null default 'upload'
                 check (source in ('upload','generated','scanned','signed','imported','ocr')),
  generated_from_template_id uuid,
  created_at     timestamptz not null default now(),
  created_by     uuid
);

create unique index document_version_uq on documents.document_version (document_id, version);
create index document_version_hash_ix on documents.document_version (tenant_id, sha256);

create trigger document_version_immutable before update or delete on documents.document_version
  for each row execute function app.deny_mutation();

alter table documents.document
  add constraint document_current_version_fk
  foreign key (current_version_id) references documents.document_version(id);

--------------------------------------------------------------------------------
-- Templates — the source of generated paperwork.
--------------------------------------------------------------------------------
create table documents.template (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid references platform.tenant(id),   -- NULL = system template
  code           text not null,
  name           text not null,
  category       text not null,
  state_code     char(2),          -- state-specific forms (OK/KS/TX at launch)
  engine         text not null default 'html' check (engine in ('html','pdf_form','docx')),
  body           text,             -- HTML/handlebars source
  object_key     text,             -- for pdf_form / docx templates
  field_map      jsonb not null default '{}'::jsonb,
  signature_fields jsonb not null default '[]'::jsonb,  -- anchors for signer placement
  version        int not null default 1,
  is_active      boolean not null default true,
  requires_signature boolean not null default false,
  created_at     timestamptz not null default now(),
  created_by     uuid,
  updated_at     timestamptz not null default now()
);

create unique index template_code_uq
  on documents.template (coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid),
                         code, version);
create index template_state_ix on documents.template (state_code, category) where is_active;

--------------------------------------------------------------------------------
-- OCR results. Original image, raw text, structured extraction, corrections,
-- and confidence — all four retained per the constitution.
--------------------------------------------------------------------------------
create table documents.ocr_result (
  id                 uuid primary key default gen_random_uuid(),
  tenant_id          uuid not null references platform.tenant(id),
  document_version_id uuid not null references documents.document_version(id) on delete cascade,
  document_type      text not null,     -- drivers_license | title | registration | insurance | ...
  status             text not null default 'queued'
                     check (status in ('queued','preprocessing','ocr','extracting','review','accepted','failed')),
  engine             text,              -- paddleocr version
  engine_version     text,
  raw_text           text,
  raw_blocks         jsonb,             -- per-block text + bbox + confidence
  extracted          jsonb,             -- LLM structured extraction
  extraction_model   text,
  extraction_prompt_version text,
  corrected          jsonb,             -- human corrections; extraction is never overwritten
  corrected_by       uuid,
  corrected_at       timestamptz,
  overall_confidence numeric(5,4) check (overall_confidence between 0 and 1),
  field_confidence   jsonb,
  error_message      text,
  processing_ms      int,
  created_at         timestamptz not null default now(),
  completed_at       timestamptz
);

create index ocr_result_version_ix on documents.ocr_result (tenant_id, document_version_id);
-- Low-confidence extractions are a work queue, not a silent failure.
create index ocr_result_review_ix
  on documents.ocr_result (tenant_id, status, overall_confidence)
  where status in ('review','failed');

comment on table documents.ocr_result is
  'Driver-license extraction is subject to state restrictions on capture, use, '
  'and retention (RISK-LEGAL-002). Retention for document_type = drivers_license '
  'is governed by the tenant retention policy and must be reviewed before this '
  'module ships in any state.';

--------------------------------------------------------------------------------
-- E-signature (ADR-0009)
--------------------------------------------------------------------------------
create table signatures.envelope (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references platform.tenant(id),
  deal_id           uuid references sales.deal(id),
  customer_id       uuid references crm.customer(id),
  name              text not null,
  message           text,
  status            text not null default 'draft'
                    check (status in ('draft','sent','in_progress','completed','declined',
                                      'voided','expired')),
  mode              text not null default 'remote' check (mode in ('in_person','remote','mixed')),
  sent_at           timestamptz,
  completed_at      timestamptz,
  voided_at         timestamptz,
  void_reason       text,
  expires_at        timestamptz,
  -- Hash of the completed, flattened package. Chained into audit.event.
  completed_hash    bytea,
  completed_object_key text,
  reminder_count    int not null default 0,
  last_reminder_at  timestamptz,
  created_at        timestamptz not null default now(),
  created_by        uuid,
  updated_at        timestamptz not null default now()
);

create index envelope_deal_ix on signatures.envelope (tenant_id, deal_id);
create index envelope_status_ix on signatures.envelope (tenant_id, status, sent_at desc);

create table signatures.envelope_document (
  id                  uuid primary key default gen_random_uuid(),
  tenant_id           uuid not null references platform.tenant(id),
  envelope_id         uuid not null references signatures.envelope(id) on delete cascade,
  document_version_id uuid not null references documents.document_version(id),
  sort_order          int not null default 0,
  -- Integrity: hash BEFORE signing and hash of the signed artifact. Association
  -- is to a specific document VERSION, never a document family.
  pre_sign_hash       bytea not null,
  signed_hash         bytea,
  signed_object_key   text,
  signed_at           timestamptz
);

create index envelope_document_envelope_ix
  on signatures.envelope_document (envelope_id, sort_order);

create table signatures.signer (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references platform.tenant(id),
  envelope_id       uuid not null references signatures.envelope(id) on delete cascade,
  customer_id       uuid references crm.customer(id),
  user_id           uuid references identity."user"(id),
  role              text not null
                    check (role in ('buyer','co_buyer','seller','dealer_rep','witness','notary')),
  full_name         text not null,
  email             citext,
  phone             text,
  sort_order        int not null default 0,
  status            text not null default 'pending'
                    check (status in ('pending','sent','viewed','signed','declined','expired')),

  -- ESIGN/UETA consent: captured BEFORE signing, versioned, revocable.
  consent_given_at    timestamptz,
  consent_disclosure_version text,
  consent_ip          inet,
  consent_user_agent  text,
  consent_withdrawn_at timestamptz,

  -- Attribution: how we know this is the person.
  auth_method       text check (auth_method in ('email_otp','sms_otp','access_code',
                                                'authenticated_session','in_person_witnessed')),
  auth_verified_at  timestamptz,
  access_token_hash bytea,
  access_attempts   int not null default 0,

  -- Intent + environment, captured at the moment of signing.
  signed_at         timestamptz,
  declined_at       timestamptz,
  decline_reason    text,
  signature_image_key text,
  initials_image_key  text,
  typed_name        text,
  signature_method  text check (signature_method in ('drawn','typed','uploaded')),
  ip_address        inet,
  user_agent        text,
  device_label      text,
  timezone_offset_minutes int,
  geolocation       jsonb,          -- only when explicitly consented

  created_at        timestamptz not null default now()
);

create index signer_envelope_ix on signatures.signer (envelope_id, sort_order);
create unique index signer_access_token_uq
  on signatures.signer (access_token_hash) where access_token_hash is not null;

comment on column signatures.signer.consent_given_at is
  'ESIGN requires affirmative consent to electronic records BEFORE the record is '
  'presented for signature, with disclosure of hardware/software requirements and '
  'the right to withdraw. An envelope may not transition to in_progress for a '
  'signer whose consent_given_at is null.';

-- Every observable event in the signing ceremony. This table IS the evidence
-- package; it is append-only and chained into audit.event on completion.
create table signatures.audit_entry (
  id           bigint generated always as identity primary key,
  tenant_id    uuid not null references platform.tenant(id),
  envelope_id  uuid not null references signatures.envelope(id) on delete cascade,
  signer_id    uuid references signatures.signer(id),
  event        text not null,     -- created | sent | delivered | opened | consent_given
                                  -- | authenticated | viewed_document | signed | declined
                                  -- | completed | voided | reminder_sent | download
  occurred_at  timestamptz not null default now(),
  ip_address   inet,
  user_agent   text,
  document_version_id uuid references documents.document_version(id),
  detail       jsonb not null default '{}'::jsonb,
  hash         bytea
);

create index signature_audit_envelope_ix
  on signatures.audit_entry (envelope_id, occurred_at);

create trigger signature_audit_immutable before update or delete on signatures.audit_entry
  for each row execute function app.deny_mutation();

--------------------------------------------------------------------------------
-- Messaging
--------------------------------------------------------------------------------
create table messaging.thread (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  channel      text not null check (channel in ('email','sms','internal')),
  subject      text,
  customer_id  uuid references crm.customer(id),
  lead_id      uuid references crm.lead(id),
  deal_id      uuid references sales.deal(id),
  vehicle_id   uuid references inventory.vehicle(id),
  assigned_to  uuid references identity."user"(id),
  status       text not null default 'open' check (status in ('open','snoozed','closed')),
  last_message_at timestamptz,
  unread_count int not null default 0,
  created_at   timestamptz not null default now(),
  updated_at   timestamptz not null default now()
);

create index thread_customer_ix on messaging.thread (tenant_id, customer_id, last_message_at desc);
create index thread_open_ix on messaging.thread (tenant_id, status, last_message_at desc);

create table messaging.message (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  thread_id      uuid not null references messaging.thread(id) on delete cascade,
  direction      text not null check (direction in ('inbound','outbound')),
  channel        text not null check (channel in ('email','sms','internal')),
  from_address   text,
  to_address     text,
  subject        text,
  body_text      text,
  body_html      text,
  status         text not null default 'queued'
                 check (status in ('queued','sending','sent','delivered','failed','bounced','received')),
  provider       text,
  provider_message_id text,
  error_code     text,
  error_message  text,
  -- AI-drafted messages are drafts until a human sends them (ADR-0004).
  is_ai_draft    boolean not null default false,
  approved_by    uuid,
  approved_at    timestamptz,
  sent_at        timestamptz,
  delivered_at   timestamptz,
  read_at        timestamptz,
  sent_by        uuid,
  created_at     timestamptz not null default now()
);

create index message_thread_ix on messaging.message (tenant_id, thread_id, created_at);
create index message_provider_ix on messaging.message (provider_message_id)
  where provider_message_id is not null;
create index message_pending_ix on messaging.message (tenant_id, status)
  where status in ('queued','sending');

--------------------------------------------------------------------------------
-- Publishing / syndication (Phase 1 §8: feed-based, not API-based)
--------------------------------------------------------------------------------
create table publishing.channel (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  code           text not null,        -- website | cars_com | autotrader | cargurus | facebook_export
  name           text not null,
  transport      text not null check (transport in ('http_pull','sftp_push','ftp_push','api','manual_export')),
  format         text not null check (format in ('json','xml','csv','tsv')),
  endpoint       text,
  -- Credentials are envelope-encrypted, never plaintext, never in config.
  credentials_enc bytea,
  credentials_kid text,
  field_map      jsonb not null default '{}'::jsonb,
  schedule_cron  text,
  is_enabled     boolean not null default false,
  last_run_at    timestamptz,
  last_success_at timestamptz,
  last_error     text,
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now()
);

create unique index publishing_channel_uq on publishing.channel (tenant_id, code);

create table publishing.listing (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid not null references platform.tenant(id),
  channel_id    uuid not null references publishing.channel(id) on delete cascade,
  vehicle_id    uuid not null references inventory.vehicle(id) on delete cascade,
  status        text not null default 'pending'
                check (status in ('pending','published','updating','removed','failed','excluded')),
  external_id   text,
  external_url  text,
  payload_hash  bytea,       -- skip re-publishing when nothing changed
  published_at  timestamptz,
  removed_at    timestamptz,
  last_attempt_at timestamptz,
  attempt_count int not null default 0,
  error_message text,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);

create unique index listing_channel_vehicle_uq on publishing.listing (channel_id, vehicle_id);
create index listing_status_ix on publishing.listing (tenant_id, status, last_attempt_at);

--------------------------------------------------------------------------------
-- AI generation ledger — every model call, for cost control (ADR-0004) and for
-- the ability to answer "why did the system say that?" months later.
--------------------------------------------------------------------------------
create table app.ai_generation (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references platform.tenant(id),
  feature           text not null,      -- vehicle_description | pricing | lead_summary | reply_draft
  entity_type       text,
  entity_id         uuid,
  provider          text not null,
  model             text not null,
  prompt_version    text not null,
  input_tokens      int,
  output_tokens     int,
  cost_usd          numeric(12,6),
  latency_ms        int,
  status            text not null check (status in ('ok','refused','error','timeout','quota_exceeded')),
  error_message     text,
  input_hash        bytea,
  output_text       text,
  -- Human review gate. Nothing consumer-facing publishes without this.
  approved_at       timestamptz,
  approved_by       uuid,
  rejected_at       timestamptz,
  edited_output     text,
  requested_by      uuid,
  created_at        timestamptz not null default now()
);

create index ai_generation_tenant_time_ix on app.ai_generation (tenant_id, created_at desc);
create index ai_generation_entity_ix on app.ai_generation (tenant_id, entity_type, entity_id);
create index ai_generation_cost_ix on app.ai_generation (tenant_id, created_at) include (cost_usd);

--------------------------------------------------------------------------------
-- Notifications and saved reports
--------------------------------------------------------------------------------
create table app.notification (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  user_id      uuid not null references identity."user"(id) on delete cascade,
  type         text not null,
  title        text not null,
  body         text,
  link         text,
  severity     text not null default 'info' check (severity in ('info','success','warning','error')),
  read_at      timestamptz,
  created_at   timestamptz not null default now()
);

create index notification_user_unread_ix
  on app.notification (tenant_id, user_id, created_at desc) where read_at is null;

create table app.saved_report (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  code         text not null,           -- inventory_aging | gross_profit | ...
  name         text not null,
  filters      jsonb not null default '{}'::jsonb,
  columns      jsonb,
  schedule_cron text,
  recipients   text[],
  format       text default 'pdf' check (format in ('pdf','csv','xlsx')),
  is_shared    boolean not null default false,
  owner_id     uuid references identity."user"(id),
  last_run_at  timestamptz,
  created_at   timestamptz not null default now(),
  updated_at   timestamptz not null default now()
);

create index saved_report_tenant_ix on app.saved_report (tenant_id, code);

--------------------------------------------------------------------------------
-- Tenant settings and retention policy
--------------------------------------------------------------------------------
create table platform.tenant_setting (
  tenant_id  uuid not null references platform.tenant(id) on delete cascade,
  key        text not null,
  value      jsonb not null,
  updated_at timestamptz not null default now(),
  updated_by uuid,
  primary key (tenant_id, key)
);

create table platform.retention_policy (
  tenant_id       uuid not null references platform.tenant(id) on delete cascade,
  category        text not null,       -- matches documents.document.category, or '*'
  retain_years    int not null check (retain_years > 0),
  purge_enabled   boolean not null default false,
  updated_at      timestamptz not null default now(),
  primary key (tenant_id, category)
);

comment on table platform.retention_policy is
  'Deal jackets are commonly required to be retained 4-7 years depending on '
  'state. purge_enabled defaults to false: nothing is ever auto-deleted until a '
  'tenant explicitly opts in, and legal_hold on a document always wins.';
