--------------------------------------------------------------------------------
-- Cross-tenant isolation probe
--
-- Proves that a session authenticated as tenant B cannot read, update, delete,
-- or insert into tenant A's data — even when it knows A's primary keys.
--
-- This runs as mautodesk_app, the same role the application uses. If it ever
-- passes as a superuser but fails here, RLS is not doing what we think.
--
-- Run: psql -U postgres -d mautodesk -v ON_ERROR_STOP=1 -f isolation_probe.sql
--------------------------------------------------------------------------------

\set ON_ERROR_STOP on

-- Seed two tenants as the owner (bypasses RLS, which is the point: we are
-- constructing the fixture, not testing the setup).
insert into platform.tenant (id, slug, legal_name, state_code) values
  ('aaaaaaaa-0000-4000-8000-000000000001', 'alpha-motors', 'Alpha Motors LLC', 'OK'),
  ('bbbbbbbb-0000-4000-8000-000000000002', 'bravo-auto',   'Bravo Auto Sales', 'TX')
on conflict do nothing;

insert into inventory.vehicle (id, tenant_id, stock_number, vin, model_year, make, model, list_price)
values
  ('11111111-1111-4111-8111-000000000001','aaaaaaaa-0000-4000-8000-000000000001',
   'A-1001','1FTFW1ET5DFA00001',2019,'Ford','F-150', 28995.00),
  ('22222222-2222-4222-8222-000000000002','bbbbbbbb-0000-4000-8000-000000000002',
   'B-2001','2HGFC2F59KH500002',2020,'Honda','Civic', 18995.00)
on conflict do nothing;

insert into crm.customer (id, tenant_id, first_name, last_name, email) values
  ('33333333-3333-4333-8333-000000000003','aaaaaaaa-0000-4000-8000-000000000001',
   'Alice','Anderson','alice@example.test'),
  ('44444444-4444-4444-8444-000000000004','bbbbbbbb-0000-4000-8000-000000000002',
   'Bob','Brown','bob@example.test')
on conflict do nothing;

grant mautodesk_app to current_user;

--------------------------------------------------------------------------------
do $probe$
declare
  a_tenant  constant uuid := 'aaaaaaaa-0000-4000-8000-000000000001';
  b_tenant  constant uuid := 'bbbbbbbb-0000-4000-8000-000000000002';
  a_vehicle constant uuid := '11111111-1111-4111-8111-000000000001';
  a_customer constant uuid := '33333333-3333-4333-8333-000000000003';
  n int;
  failures int := 0;
begin
  set local role mautodesk_app;

  ---------------------------------------------------------------------------
  raise notice '--- 1. No tenant context set: everything must be invisible ---';
  perform set_config('app.tenant_id', '', true);

  select count(*) into n from inventory.vehicle;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: saw % vehicles with no tenant context (expected 0)', n;
  else raise notice 'PASS: no vehicles visible without tenant context'; end if;

  select count(*) into n from crm.customer;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: saw % customers with no tenant context', n;
  else raise notice 'PASS: no customers visible without tenant context'; end if;

  ---------------------------------------------------------------------------
  raise notice '--- 2. As tenant B: tenant A rows must be invisible by ID ---';
  perform set_config('app.tenant_id', b_tenant::text, true);

  select count(*) into n from inventory.vehicle where id = a_vehicle;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B read tenant A vehicle by primary key';
  else raise notice 'PASS: tenant A vehicle invisible to tenant B'; end if;

  select count(*) into n from crm.customer where id = a_customer;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B read tenant A customer by primary key';
  else raise notice 'PASS: tenant A customer invisible to tenant B'; end if;

  select count(*) into n from inventory.vehicle;
  if n <> 1 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % vehicles (expected exactly its own 1)', n;
  else raise notice 'PASS: tenant B sees only its own vehicle'; end if;

  ---------------------------------------------------------------------------
  raise notice '--- 3. As tenant B: writes against tenant A rows must not land ---';

  update inventory.vehicle set list_price = 1.00 where id = a_vehicle;
  get diagnostics n = row_count;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B updated % of tenant A rows', n;
  else raise notice 'PASS: tenant B update against tenant A affected 0 rows'; end if;

  delete from inventory.vehicle where id = a_vehicle;
  get diagnostics n = row_count;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B deleted % of tenant A rows', n;
  else raise notice 'PASS: tenant B delete against tenant A affected 0 rows'; end if;

  ---------------------------------------------------------------------------
  raise notice '--- 4. As tenant B: cannot forge a row into tenant A ---';
  begin
    insert into inventory.vehicle (tenant_id, stock_number, make, model)
    values (a_tenant, 'FORGED-1', 'Ford', 'Forged');
    failures := failures + 1;
    raise warning 'FAIL: tenant B inserted a row carrying tenant A''s id';
  exception when insufficient_privilege or check_violation then
    raise notice 'PASS: cross-tenant insert rejected (%)', sqlerrm;
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 5. Tenant B cannot move its own row into tenant A ---';
  begin
    update inventory.vehicle set tenant_id = a_tenant
     where id = '22222222-2222-4222-8222-000000000002';
    failures := failures + 1;
    raise warning 'FAIL: tenant B reassigned its row to tenant A';
  exception when insufficient_privilege or check_violation then
    raise notice 'PASS: tenant reassignment rejected (%)', sqlerrm;
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 6. platform.tenant exposes only the caller''s own row ---';
  select count(*) into n from platform.tenant;
  if n <> 1 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % tenant rows (expected 1)', n;
  else raise notice 'PASS: tenant B sees only its own tenant row'; end if;

  ---------------------------------------------------------------------------
  raise notice '--- 7. Append-only tables reject mutation ---';
  perform set_config('app.tenant_id', a_tenant::text, true);
  insert into audit.event (tenant_id, actor_type, action)
  values (a_tenant, 'system', 'test.probe');

  begin
    update audit.event set action = 'tampered' where tenant_id = a_tenant;
    failures := failures + 1;
    raise warning 'FAIL: audit.event accepted an UPDATE';
  exception when insufficient_privilege then
    raise notice 'PASS: audit.event rejected UPDATE';
  end;

  begin
    delete from audit.event where tenant_id = a_tenant;
    failures := failures + 1;
    raise warning 'FAIL: audit.event accepted a DELETE';
  exception when insufficient_privilege then
    raise notice 'PASS: audit.event rejected DELETE';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 8. Audit hash chain is intact ---';
  select count(*) into n from audit.verify_chain(a_tenant);
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: audit chain broken at % point(s)', n;
  else raise notice 'PASS: audit chain verified'; end if;

  ---------------------------------------------------------------------------
  reset role;
  if failures > 0 then
    raise exception 'ISOLATION PROBE FAILED: % check(s) did not pass', failures;
  end if;
  raise notice '=== ISOLATION PROBE PASSED ===';
end
$probe$;
