--------------------------------------------------------------------------------
-- MautoDesk — V0008 Authentication support
--
-- Two additions the Phase 8 domain needs, and one carefully scoped escape
-- hatch for the single query in the system that legitimately has no tenant.
--------------------------------------------------------------------------------

-- Exponential lockout needs to remember how many times an account has locked,
-- not just how many attempts failed since the last one.
alter table identity."user"
  add column if not exists lockout_count int not null default 0;

-- TOTP replay prevention: a code is valid for its entire 30-second step, so
-- without recording the accepted step an observed code can be reused inside
-- that window.
alter table identity.mfa_factor
  add column if not exists last_accepted_step bigint;

--------------------------------------------------------------------------------
-- identity.find_user_for_authentication
--
-- THE ONE PLACE THE TENANT BOUNDARY IS CROSSED, AND IT IS DELIBERATE.
--
-- At login there is no tenant context, because the tenant is derived FROM the
-- user being authenticated. Every RLS policy compares against
-- app.current_tenant_id(), which is NULL at that moment, so an ordinary query
-- returns nothing and login could never work.
--
-- The alternatives, and why they were rejected:
--
--   * Give the application role BYPASSRLS — hands the whole application a
--     master key to every tenant's data to solve one query. Absolutely not.
--   * Use a second superuser connection for login — same problem, slightly
--     better hidden.
--   * Put the tenant in the login request — that is exactly the header-supplied
--     tenancy ADR-0002 forbids, and it lets a caller probe which tenant an
--     address belongs to.
--
-- So: a SECURITY DEFINER function, which runs with the privileges of its owner
-- rather than its caller. It is deliberately narrow:
--
--   * it takes only an email address,
--   * it returns ONLY a user id and a tenant id — no password hash, no name, no
--     status, nothing else. Everything the authentication decision needs is then
--     read back through the ordinary tenant-scoped path, because by that point
--     the tenant IS known,
--   * it returns at most one row,
--   * search_path is pinned so it cannot be hijacked by a caller-controlled
--     schema,
--   * EXECUTE is granted to the application role and to nobody else.
--
-- It is the smallest hole that makes login possible, and it is auditable in one
-- place. Widening it — adding a column, relaxing the filter — is a security
-- change and should be reviewed as one.
--------------------------------------------------------------------------------
drop function if exists identity.find_user_for_authentication(citext);

create function identity.find_user_for_authentication(p_email citext)
returns table (
  user_id   uuid,
  tenant_id uuid
)
language sql
stable
security definer
set search_path = identity, pg_temp
as $$
  select u.id, u.tenant_id
    from identity."user" u
    join platform.tenant t on t.id = u.tenant_id
   where u.email = p_email
     and u.deleted_at is null
     and t.deleted_at is null
     and t.status <> 'cancelled'
   limit 1;
$$;

revoke all on function identity.find_user_for_authentication(citext) from public;
grant execute on function identity.find_user_for_authentication(citext) to mautodesk_app;

comment on function identity.find_user_for_authentication(citext) is
  'SECURITY DEFINER. The only cross-tenant read in the system, needed because '
  'the tenant is derived from the user at login. Returns authentication columns '
  'only, for at most one user. Do not add columns without a security review.';

--------------------------------------------------------------------------------
-- Login attempts are recorded for addresses that do not exist, so enumeration
-- attempts are visible in the security log. The API response never
-- distinguishes the cases; this table does.
--------------------------------------------------------------------------------
create or replace function identity.record_login_attempt(
  p_tenant_id uuid,
  p_email citext,
  p_succeeded boolean,
  p_failure_reason text,
  p_ip inet,
  p_user_agent text)
returns void
language sql
security definer
set search_path = identity, pg_temp
as $$
  insert into identity.login_attempt
    (tenant_id, email, succeeded, failure_reason, ip_address, user_agent)
  values (p_tenant_id, p_email, p_succeeded, p_failure_reason, p_ip, p_user_agent);
$$;

revoke all on function identity.record_login_attempt(uuid, citext, boolean, text, inet, text) from public;
grant execute on function identity.record_login_attempt(uuid, citext, boolean, text, inet, text) to mautodesk_app;

comment on function identity.record_login_attempt(uuid, citext, boolean, text, inet, text) is
  'SECURITY DEFINER: a failed attempt for an unknown address has no tenant, so '
  'it cannot be written through a tenant-scoped policy. Insert-only.';
