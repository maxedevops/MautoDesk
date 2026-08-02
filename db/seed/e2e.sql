--------------------------------------------------------------------------------
-- Seed data for the end-to-end suite.
--
-- Idempotent. The password hash below is a real Argon2id hash of the E2E
-- password, produced by the production hasher — not a placeholder — so the
-- suite exercises the same verification path a dealer does.
--
-- This account exists only in development and CI databases.
--------------------------------------------------------------------------------

insert into platform.tenant (id, slug, legal_name, state_code)
values ('cccccccc-0000-4000-8000-00000000c001', 'ridgeline-auto', 'Ridgeline Auto LLC', 'OK')
on conflict (id) do nothing;

insert into identity.role (id, tenant_id, code, name, is_system)
values ('dddddddd-0000-4000-8000-00000000d001', 'cccccccc-0000-4000-8000-00000000c001',
        'owner-demo', 'Owner', false)
on conflict (id) do nothing;

insert into identity.role_permission (role_id, permission_code)
select 'dddddddd-0000-4000-8000-00000000d001', code
  from identity.permission
 where module = 'inventory'
on conflict do nothing;

insert into identity."user"
    (id, tenant_id, email, password_hash, first_name, last_name, status, email_verified_at)
values ('eeeeeeee-0000-4000-8000-00000000e001', 'cccccccc-0000-4000-8000-00000000c001',
        'dana@ridgeline.test',
        '$argon2id$v=19$m=19456,t=2,p=1$fwkSVoCSGHh+CwhmmNkubg==$S6HHAZ72Jsl1bpRx7MepL/ssOoDVU3QYqZ7DxFxHV5c=',
        'Dana', 'Reyes', 'active', now())
on conflict (id) do update
  set password_hash      = excluded.password_hash,
      mfa_enrolled_at    = null,
      failed_login_count = 0,
      locked_until       = null;

insert into identity.user_role (tenant_id, user_id, role_id)
values ('cccccccc-0000-4000-8000-00000000c001',
        'eeeeeeee-0000-4000-8000-00000000e001',
        'dddddddd-0000-4000-8000-00000000d001')
on conflict do nothing;

-- The suite enrols MFA and captures the secret, so the account must start
-- unenrolled on every run.
delete from identity.mfa_factor
 where user_id = 'eeeeeeee-0000-4000-8000-00000000e001';

-- Inventory for the grid to render.
insert into inventory.vehicle
    (tenant_id, stock_number, vin, model_year, make, model, trim, mileage,
     exterior_color, status, list_price, acquired_at, is_published)
values
 ('cccccccc-0000-4000-8000-00000000c001', 'A-0994', '1C6SRFFT2KN512204', 2019, 'RAM',
  '1500 Big Horn', 'Crew Cab', 88420, 'Bright White', 'available', 28995.00, current_date - 104, true),
 ('cccccccc-0000-4000-8000-00000000c001', 'A-1188', '1FTFW1ET5MFA48219', 2021, 'Ford',
  'F-150', 'XLT', 46910, 'Oxford White', 'available', 38450.00, current_date - 44, true)
on conflict do nothing;
