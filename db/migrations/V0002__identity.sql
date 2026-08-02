--------------------------------------------------------------------------------
-- MautoDesk — V0002 Identity
--
-- Users, the permission/role model (ADR §5), sessions, refresh-token families
-- with reuse detection, MFA, invitations, and login-attempt tracking.
--
-- Design notes that matter:
--   * A user belongs to exactly one tenant. Multi-tenant staff (a consultant
--     serving several dealers) get one account per tenant. This keeps the
--     token's tenant claim unambiguous — see ADR-0002's rule that tenancy is
--     resolved from the token and nothing else.
--   * Password hashes are Argon2id. The `password_algorithm` column exists so a
--     future parameter bump can rehash on next successful login rather than
--     forcing a reset.
--   * Refresh tokens are stored HASHED. A stolen database does not yield usable
--     tokens. Reuse of a rotated token revokes the whole family.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Permissions are seeded rows, not an enum, so a new module can add its atoms
-- in its own migration without altering a global type.
--------------------------------------------------------------------------------
create table identity.permission (
  code         text primary key,          -- 'inventory.vehicle.write'
  module       text not null,             -- 'inventory'
  description  text not null,
  is_sensitive boolean not null default false,  -- gates cost/PII visibility
  created_at   timestamptz not null default now()
);

comment on column identity.permission.is_sensitive is
  'Marks permissions whose grant must be explicitly audited, e.g. vehicle cost '
  'visibility and customer PII access. Granting one of these emits an audit event.';

create table identity.role (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid references platform.tenant(id),  -- NULL = system role template
  code         text not null,
  name         text not null,
  description  text,
  is_system    boolean not null default false,       -- seeded; cannot be deleted
  created_at   timestamptz not null default now(),
  created_by   uuid,
  updated_at   timestamptz not null default now(),
  updated_by   uuid,
  deleted_at   timestamptz,
  deleted_by   uuid
);

create unique index role_tenant_code_uq
  on identity.role (coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), code)
  where deleted_at is null;

create table identity.role_permission (
  role_id         uuid not null references identity.role(id) on delete cascade,
  permission_code text not null references identity.permission(code),
  granted_at      timestamptz not null default now(),
  granted_by      uuid,
  primary key (role_id, permission_code)
);

--------------------------------------------------------------------------------
-- identity.user
--------------------------------------------------------------------------------
create table identity."user" (
  id                    uuid primary key default gen_random_uuid(),
  tenant_id             uuid not null references platform.tenant(id),
  email                 citext not null,
  email_verified_at     timestamptz,
  password_hash         text,                    -- NULL for SSO-only users
  password_algorithm    text not null default 'argon2id',
  password_changed_at   timestamptz,
  must_change_password  boolean not null default false,
  first_name            text not null,
  last_name             text not null,
  display_name          text generated always as (first_name || ' ' || last_name) stored,
  phone                 text,
  phone_verified_at     timestamptz,
  avatar_object_key     text,
  job_title             text,
  -- FTC Safeguards requires MFA for everyone with access to customer information.
  -- It is therefore a platform obligation, not a tenant preference; this column
  -- records enrolment state, never whether MFA is "enabled" for the account.
  mfa_enrolled_at       timestamptz,
  status                text not null default 'invited'
                        check (status in ('invited','active','suspended','locked','deactivated')),
  failed_login_count    int not null default 0,
  locked_until          timestamptz,
  last_login_at         timestamptz,
  last_login_ip         inet,
  timezone              text,
  locale                text not null default 'en-US',
  created_at            timestamptz not null default now(),
  created_by            uuid,
  updated_at            timestamptz not null default now(),
  updated_by            uuid,
  deleted_at            timestamptz,
  deleted_by            uuid
);

create unique index user_tenant_email_uq
  on identity."user" (tenant_id, email) where deleted_at is null;
create index user_tenant_status_ix
  on identity."user" (tenant_id, status) where deleted_at is null;

create trigger user_set_updated_at before update on identity."user"
  for each row execute function app.set_updated_at();
create trigger user_enforce_tenant before insert or update on identity."user"
  for each row execute function app.enforce_tenant();

create table identity.user_role (
  tenant_id   uuid not null references platform.tenant(id),
  user_id     uuid not null references identity."user"(id) on delete cascade,
  role_id     uuid not null references identity.role(id),
  assigned_at timestamptz not null default now(),
  assigned_by uuid,
  primary key (user_id, role_id)
);

create index user_role_tenant_ix on identity.user_role (tenant_id, role_id);

-- Row-level scope grants beyond tenancy (ADR §5 level 3), e.g. a sales manager
-- who may see all leads rather than only their own.
create table identity.user_scope (
  tenant_id  uuid not null references platform.tenant(id),
  user_id    uuid not null references identity."user"(id) on delete cascade,
  scope      text not null,            -- 'crm.lead.read.all', 'sales.deal.read.all'
  granted_at timestamptz not null default now(),
  granted_by uuid,
  primary key (user_id, scope)
);

--------------------------------------------------------------------------------
-- MFA
--------------------------------------------------------------------------------
create table identity.mfa_factor (
  id             uuid primary key default gen_random_uuid(),
  tenant_id      uuid not null references platform.tenant(id),
  user_id        uuid not null references identity."user"(id) on delete cascade,
  type           text not null check (type in ('totp','webauthn','recovery_code')),
  label          text,
  -- TOTP secrets and WebAuthn material are envelope-encrypted, never plaintext.
  secret_enc     bytea,
  secret_kid     text,
  credential_id  bytea,                -- WebAuthn
  public_key     bytea,                -- WebAuthn
  sign_count     bigint,               -- WebAuthn clone detection
  confirmed_at   timestamptz,
  last_used_at   timestamptz,
  created_at     timestamptz not null default now(),
  revoked_at     timestamptz
);

create index mfa_factor_user_ix on identity.mfa_factor (user_id) where revoked_at is null;
create unique index mfa_factor_credential_uq
  on identity.mfa_factor (credential_id) where credential_id is not null;

-- Recovery codes are single-use and stored hashed, one row per code.
create table identity.mfa_recovery_code (
  id         uuid primary key default gen_random_uuid(),
  tenant_id  uuid not null references platform.tenant(id),
  user_id    uuid not null references identity."user"(id) on delete cascade,
  code_hash  text not null,
  used_at    timestamptz,
  created_at timestamptz not null default now()
);

create index mfa_recovery_code_user_ix
  on identity.mfa_recovery_code (user_id) where used_at is null;

--------------------------------------------------------------------------------
-- Sessions and refresh-token families
--
-- A "family" is one login. Rotation issues a new token in the same family and
-- marks the old one rotated. Presenting an already-rotated token means the token
-- leaked: revoke the entire family, raise a security event, and force re-auth.
--------------------------------------------------------------------------------
create table identity.session (
  id              uuid primary key default gen_random_uuid(),
  tenant_id       uuid not null references platform.tenant(id),
  user_id         uuid not null references identity."user"(id) on delete cascade,
  family_id       uuid not null default gen_random_uuid(),
  created_at      timestamptz not null default now(),
  last_seen_at    timestamptz not null default now(),
  expires_at      timestamptz not null,
  revoked_at      timestamptz,
  revoked_reason  text,           -- logout | rotation_reuse | admin | password_change | expiry
  ip_address      inet,
  user_agent      text,
  device_label    text,
  mfa_satisfied_at timestamptz,
  amr             text[] not null default '{}'   -- auth methods: pwd, totp, webauthn, oidc
);

create index session_user_active_ix
  on identity.session (user_id, last_seen_at desc) where revoked_at is null;
create index session_family_ix on identity.session (family_id);

create table identity.refresh_token (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  session_id   uuid not null references identity.session(id) on delete cascade,
  family_id    uuid not null,
  token_hash   bytea not null,          -- sha256 of the opaque token; never the token
  issued_at    timestamptz not null default now(),
  expires_at   timestamptz not null,
  rotated_at   timestamptz,
  replaced_by  uuid references identity.refresh_token(id),
  revoked_at   timestamptz,
  used_from_ip inet
);

create unique index refresh_token_hash_uq on identity.refresh_token (token_hash);
create index refresh_token_family_ix on identity.refresh_token (family_id, issued_at desc);

--------------------------------------------------------------------------------
-- Invitations, password reset, email verification — all single-use, hashed,
-- short-lived. Never store a usable token.
--------------------------------------------------------------------------------
create table identity.user_invitation (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  email        citext not null,
  role_id      uuid not null references identity.role(id),
  token_hash   bytea not null,
  invited_by   uuid not null,
  expires_at   timestamptz not null,
  accepted_at  timestamptz,
  accepted_user_id uuid references identity."user"(id),
  revoked_at   timestamptz,
  created_at   timestamptz not null default now()
);

create unique index user_invitation_token_uq on identity.user_invitation (token_hash);
create index user_invitation_pending_ix
  on identity.user_invitation (tenant_id, email)
  where accepted_at is null and revoked_at is null;

create table identity.one_time_token (
  id          uuid primary key default gen_random_uuid(),
  tenant_id   uuid references platform.tenant(id),
  user_id     uuid references identity."user"(id) on delete cascade,
  purpose     text not null check (purpose in
                ('password_reset','email_verify','phone_verify','signer_access','magic_link')),
  token_hash  bytea not null,
  expires_at  timestamptz not null,
  consumed_at timestamptz,
  attempts    int not null default 0,
  metadata    jsonb not null default '{}'::jsonb,
  created_at  timestamptz not null default now()
);

create unique index one_time_token_hash_uq on identity.one_time_token (token_hash);
create index one_time_token_expiry_ix on identity.one_time_token (expires_at)
  where consumed_at is null;

--------------------------------------------------------------------------------
-- Login attempts — feeds lockout, anomaly alerting, and the security log.
-- Deliberately records failures for accounts that do not exist, so enumeration
-- attempts are visible. The API response never distinguishes the two cases.
--------------------------------------------------------------------------------
create table identity.login_attempt (
  id           bigint generated always as identity primary key,
  tenant_id    uuid,
  email        citext not null,
  succeeded    boolean not null,
  failure_reason text,   -- unknown_user | bad_password | locked | mfa_failed | suspended
  ip_address   inet,
  user_agent   text,
  attempted_at timestamptz not null default now()
);

create index login_attempt_email_time_ix on identity.login_attempt (email, attempted_at desc);
create index login_attempt_ip_time_ix    on identity.login_attempt (ip_address, attempted_at desc);

--------------------------------------------------------------------------------
-- External identity providers (OAuth/OIDC SSO)
--------------------------------------------------------------------------------
create table identity.external_login (
  id            uuid primary key default gen_random_uuid(),
  tenant_id     uuid not null references platform.tenant(id),
  user_id       uuid not null references identity."user"(id) on delete cascade,
  provider      text not null,           -- google | microsoft | okta
  subject       text not null,           -- provider's stable subject claim
  email_at_link citext,
  linked_at     timestamptz not null default now(),
  last_used_at  timestamptz
);

create unique index external_login_provider_subject_uq
  on identity.external_login (provider, subject);

--------------------------------------------------------------------------------
-- Tenant-issued API keys (for a dealer's own website or integrations).
-- Scoped to a permission set, hashed at rest, individually revocable.
--------------------------------------------------------------------------------
create table identity.api_key (
  id           uuid primary key default gen_random_uuid(),
  tenant_id    uuid not null references platform.tenant(id),
  name         text not null,
  key_prefix   text not null,            -- shown in the UI so a key is identifiable
  key_hash     bytea not null,
  scopes       text[] not null default '{}',
  created_by   uuid not null,
  created_at   timestamptz not null default now(),
  last_used_at timestamptz,
  expires_at   timestamptz,
  revoked_at   timestamptz,
  revoked_by   uuid
);

create unique index api_key_hash_uq on identity.api_key (key_hash);
create index api_key_tenant_ix on identity.api_key (tenant_id) where revoked_at is null;

--------------------------------------------------------------------------------
-- Platform administrators — a distinct principal type (ADR §5), deliberately
-- NOT rows in identity."user". A platform admin has no tenant_id and cannot be
-- confused with a dealer user by any code path.
--------------------------------------------------------------------------------
create table platform.admin_user (
  id                 uuid primary key default gen_random_uuid(),
  email              citext not null unique,
  password_hash      text not null,
  password_algorithm text not null default 'argon2id',
  first_name         text not null,
  last_name          text not null,
  mfa_enrolled_at    timestamptz,        -- mandatory before activation
  permissions        text[] not null default '{}',
  status             text not null default 'active'
                     check (status in ('active','suspended','deactivated')),
  last_login_at      timestamptz,
  created_at         timestamptz not null default now(),
  updated_at         timestamptz not null default now()
);

-- Every impersonation is time-boxed, reason-bearing, and visible to the tenant.
create table platform.impersonation_session (
  id              uuid primary key default gen_random_uuid(),
  admin_user_id   uuid not null references platform.admin_user(id),
  tenant_id       uuid not null references platform.tenant(id),
  target_user_id  uuid references identity."user"(id),
  reason          text not null,
  support_ticket  text,
  started_at      timestamptz not null default now(),
  expires_at      timestamptz not null,
  ended_at        timestamptz,
  ip_address      inet
);

create index impersonation_tenant_ix
  on platform.impersonation_session (tenant_id, started_at desc);

comment on table platform.impersonation_session is
  'Tenants can read their own rows. A dealer must be able to see when we looked '
  'at their data — this is a product commitment, not only an audit control.';
