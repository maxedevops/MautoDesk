--------------------------------------------------------------------------------
-- V0010 invariants — compliance, lending, and titling
--
-- The isolation probe proves tenants cannot see each other. This proves the
-- guarantees V0010 claims in its own header: that history cannot be rewritten,
-- that two rule versions cannot cover the same day, that an unapproved rule is
-- invisible to a resolver, that a retried submission cannot become a second
-- one, and that the RLS regime reaches the three new schemas.
--
-- Runs as mautodesk_app wherever the guarantee is one the application must not
-- be able to break. Where the fixture has to be built, it is built as the owner
-- and said so.
--
-- Run: psql -U postgres -d mautodesk -v ON_ERROR_STOP=1 -f v0010_invariants.sql
--------------------------------------------------------------------------------

\set ON_ERROR_STOP on

insert into platform.tenant (id, slug, legal_name, state_code) values
  ('aaaaaaaa-0000-4000-8000-000000000001', 'alpha-motors', 'Alpha Motors LLC', 'OK'),
  ('bbbbbbbb-0000-4000-8000-000000000002', 'bravo-auto',   'Bravo Auto Sales', 'TX')
on conflict do nothing;

-- A jurisdiction, an authority, a source and a source version, so a rule
-- version has something real to cite. Deliberately fictional: this is a test
-- fixture, and a fixture that looks like a real statute is a fixture somebody
-- eventually mistakes for one.
insert into compliance.regulatory_jurisdiction (code, level, name) values
  ('ZZ-TEST', 'state', 'Test Jurisdiction (fixture only)')
on conflict (code) do nothing;

insert into compliance.regulatory_authority (id, jurisdiction_code, code, name) values
  ('cccccccc-0000-4000-8000-000000000001', 'ZZ-TEST', 'TEST-AUTH', 'Test Authority (fixture only)')
on conflict (jurisdiction_code, code) do nothing;

insert into compliance.regulatory_source (id, authority_id, source_type, title, citation) values
  ('cccccccc-0000-4000-8000-000000000002', 'cccccccc-0000-4000-8000-000000000001',
   'statute', 'Fixture Statute', 'ZZ-TEST-FIXTURE-1')
on conflict (authority_id, citation) do nothing;

-- content_location is not decoration: the approval gate refuses any approved
-- rule whose source was never archived, and that gate has no fixture carve-out.
-- So the fixture archives its own one-word "statute" and cites it. The hash
-- below is the real sha256 of that file's seven bytes, not a placeholder.
insert into compliance.regulatory_source_version
  (id, source_id, version, retrieved_at, retrieved_by, content_hash, content_location) values
  ('cccccccc-0000-4000-8000-000000000003', 'cccccccc-0000-4000-8000-000000000002',
   1, now(), 'fixture', sha256('fixture'::bytea), 'db/tests/fixtures/zz-test-statute.txt')
on conflict do nothing;

insert into compliance.regulatory_rule (id, rule_code, jurisdiction_code, category, title) values
  ('cccccccc-0000-4000-8000-000000000004', 'ZZ-TEST-001', 'ZZ-TEST', 'title', 'Fixture rule')
on conflict (rule_code) do nothing;

-- A user and a deal, so the override and submission checks exercise the real
-- foreign keys rather than quietly inserting zero rows and passing.
insert into identity."user" (id, tenant_id, email, first_name, last_name) values
  ('dddddddd-0000-4000-8000-000000000001', 'aaaaaaaa-0000-4000-8000-000000000001',
   'fixture@example.test', 'Fix', 'Ture')
on conflict do nothing;

insert into sales.deal (id, tenant_id, deal_number) values
  ('eeeeeeee-0000-4000-8000-000000000001', 'aaaaaaaa-0000-4000-8000-000000000001', 'FIX-0001')
on conflict do nothing;

grant mautodesk_app to current_user;

--------------------------------------------------------------------------------
do $probe$
declare
  a_tenant constant uuid := 'aaaaaaaa-0000-4000-8000-000000000001';
  b_tenant constant uuid := 'bbbbbbbb-0000-4000-8000-000000000002';
  rule_id  constant uuid := 'cccccccc-0000-4000-8000-000000000004';
  src_id   constant uuid := 'cccccccc-0000-4000-8000-000000000003';
  fix_user constant uuid := 'dddddddd-0000-4000-8000-000000000001';
  fix_deal constant uuid := 'eeeeeeee-0000-4000-8000-000000000001';
  v_id     uuid;
  run_id   uuid;
  lender_id uuid;
  program_id uuid;
  n int;
  failures int := 0;
begin
  ---------------------------------------------------------------------------
  raise notice '--- 1. A rule version cannot be edited or deleted ---';
  ---------------------------------------------------------------------------
  insert into compliance.regulatory_rule_version
    (rule_id, version, effective_from, source_version_id, trigger_event,
     definition, severity, approved_at, approved_by)
  values (rule_id, '1.0.0', date '2026-01-01', src_id, 'DEAL_SOLD',
          '{"all":[]}'::jsonb, 'blocking', now(), 'fixture reviewer')
  returning id into v_id;

  begin
    update compliance.regulatory_rule_version set severity = 'warning' where id = v_id;
    failures := failures + 1;
    raise warning 'FAIL: a rule version accepted an UPDATE';
  exception when insufficient_privilege then
    raise notice 'PASS: regulatory_rule_version rejected UPDATE';
  end;

  begin
    delete from compliance.regulatory_rule_version where id = v_id;
    failures := failures + 1;
    raise warning 'FAIL: a rule version accepted a DELETE';
  exception when insufficient_privilege then
    raise notice 'PASS: regulatory_rule_version rejected DELETE';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 2. Two APPROVED versions cannot cover the same date ---';
  ---------------------------------------------------------------------------
  begin
    insert into compliance.regulatory_rule_version
      (rule_id, version, effective_from, source_version_id, trigger_event,
       definition, severity, approved_at, approved_by)
    values (rule_id, '1.1.0', date '2026-06-01', src_id, 'DEAL_SOLD',
            '{"all":[]}'::jsonb, 'blocking', now(), 'fixture reviewer');
    failures := failures + 1;
    raise warning 'FAIL: two approved rule versions overlap; the resolver could pick either';
  exception when exclusion_violation then
    raise notice 'PASS: overlapping approved rule versions rejected';
  end;

  -- An UNAPPROVED version may overlap: it is a draft of the replacement, and
  -- the evaluator cannot see it, so it is not ambiguous.
  insert into compliance.regulatory_rule_version
    (rule_id, version, effective_from, source_version_id, trigger_event,
     definition, severity)
  values (rule_id, '2.0.0-draft', date '2026-06-01', src_id, 'DEAL_SOLD',
          '{"all":[]}'::jsonb, 'blocking');
  raise notice 'PASS: an unapproved draft may overlap an approved version';

  ---------------------------------------------------------------------------
  raise notice '--- 3. An approval must name its approver ---';
  ---------------------------------------------------------------------------
  begin
    insert into compliance.regulatory_rule_version
      (rule_id, version, effective_from, source_version_id, trigger_event,
       definition, severity, approved_at)
    values (rule_id, '9.9.9', date '2030-01-01', src_id, 'DEAL_SOLD',
            '{"all":[]}'::jsonb, 'blocking', now());
    failures := failures + 1;
    raise warning 'FAIL: a rule was approved by nobody';
  exception when check_violation then
    raise notice 'PASS: approved_at without approved_by rejected';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 4. A source version cannot be rewritten ---';
  ---------------------------------------------------------------------------
  begin
    update compliance.regulatory_source_version
       set content_hash = sha256('tampered'::bytea) where id = src_id;
    failures := failures + 1;
    raise warning 'FAIL: an archived source version accepted an UPDATE';
  exception when insufficient_privilege then
    raise notice 'PASS: regulatory_source_version rejected UPDATE';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 5. Compliance runs and results are append-only ---';
  ---------------------------------------------------------------------------
  insert into compliance.compliance_run
    (tenant_id, jurisdiction_code, trigger_event, engine_version, rule_pack_version,
     evaluated_on, overall_status, input_hash, result_hash, input)
  values (a_tenant, 'ZZ-TEST', 'DEAL_SOLD', '0.1.0', 'ZZ-TEST-0.1.0',
          current_date, 'red', sha256('in'::bytea), sha256('out'::bytea), '{}'::jsonb)
  returning id into run_id;

  begin
    update compliance.compliance_run set overall_status = 'green' where id = run_id;
    failures := failures + 1;
    raise warning 'FAIL: a compliance run was restated after the fact';
  exception when insufficient_privilege then
    raise notice 'PASS: compliance_run rejected UPDATE';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 6. An override needs a reason somebody actually wrote ---';
  ---------------------------------------------------------------------------
  begin
    insert into compliance.override_request
      (tenant_id, deal_id, rule_code, rule_version, rule_version_id, reason, requested_by)
    values (a_tenant, fix_deal, 'ZZ-TEST-001', '1.0.0', v_id, 'ok', fix_user);
    failures := failures + 1;
    raise warning 'FAIL: "ok" was accepted as an override reason';
  exception when check_violation then
    raise notice 'PASS: a two-character override reason was rejected';
  end;

  -- A real reason is accepted, and only one may be live per rule per deal.
  insert into compliance.override_request
    (tenant_id, deal_id, rule_code, rule_version, rule_version_id, reason,
     requested_by, status)
  values (a_tenant, fix_deal, 'ZZ-TEST-001', '1.0.0', v_id,
          'Customer produced the inspection certificate in person.', fix_user, 'approved');

  begin
    insert into compliance.override_request
      (tenant_id, deal_id, rule_code, rule_version, rule_version_id, reason,
       requested_by, status)
    values (a_tenant, fix_deal, 'ZZ-TEST-001', '1.0.0', v_id,
            'A second approval for the very same rule on the same deal.', fix_user, 'approved');
    failures := failures + 1;
    raise warning 'FAIL: a deal carried two live overrides of one rule';
  exception when unique_violation then
    raise notice 'PASS: a second live override of the same rule was rejected';
  end;

  -- An expiry has no human actor; every other decision must have one.
  begin
    insert into compliance.override_decision (tenant_id, request_id, action)
    select a_tenant, id, 'approved' from compliance.override_request
     where tenant_id = a_tenant limit 1;
    failures := failures + 1;
    raise warning 'FAIL: an override was approved by nobody';
  exception when check_violation then
    raise notice 'PASS: an override approval with no approver was rejected';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 7. Lender program versions: append-only and non-overlapping ---';
  ---------------------------------------------------------------------------
  insert into lending.lender (tenant_id, code, name, type)
  values (a_tenant, 'FIX', 'Fixture Finance', 'bank') returning id into lender_id;
  insert into lending.lender_program (tenant_id, lender_id, code, name)
  values (a_tenant, lender_id, 'TIER1', 'Fixture Tier 1') returning id into program_id;

  insert into lending.lender_program_version
    (tenant_id, program_id, version, effective_from, eligibility, rate_sheet,
     approved_at, approved_by)
  values (a_tenant, program_id, 1, date '2026-01-01', '{}'::jsonb, '{}'::jsonb,
          now(), 'fixture reviewer')
  returning id into v_id;

  begin
    update lending.lender_program_version set rate_sheet = '{"changed":true}'::jsonb
     where id = v_id;
    failures := failures + 1;
    raise warning 'FAIL: a rate sheet was rewritten under an already-quoted deal';
  exception when insufficient_privilege then
    raise notice 'PASS: lender_program_version rejected UPDATE';
  end;

  begin
    insert into lending.lender_program_version
      (tenant_id, program_id, version, effective_from, eligibility, rate_sheet,
       approved_at, approved_by)
    values (a_tenant, program_id, 2, date '2026-03-01', '{}'::jsonb, '{}'::jsonb,
            now(), 'fixture reviewer');
    failures := failures + 1;
    raise warning 'FAIL: two approved program versions overlap in time';
  exception when exclusion_violation then
    raise notice 'PASS: overlapping approved program versions rejected';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 8. A retried submission cannot become a second one ---';
  ---------------------------------------------------------------------------
  insert into lending.lender_submission
    (tenant_id, deal_id, program_version_id, idempotency_key)
  values (a_tenant, fix_deal, v_id, 'retry-me');

  begin
    insert into lending.lender_submission
      (tenant_id, deal_id, program_version_id, idempotency_key)
    values (a_tenant, fix_deal, v_id, 'retry-me');
    failures := failures + 1;
    raise warning 'FAIL: a replayed submission created a second credit application';
  exception when unique_violation then
    raise notice 'PASS: replayed lender submission rejected by idempotency key';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 9. One live temporary tag per number ---';
  ---------------------------------------------------------------------------
  insert into titling.temporary_tag (tenant_id, tag_number, status)
  values (a_tenant, 'TAG-0001', 'issued');

  begin
    insert into titling.temporary_tag (tenant_id, tag_number, status)
    values (a_tenant, 'TAG-0001', 'assigned');
    failures := failures + 1;
    raise warning 'FAIL: the same tag number was issued twice at once';
  exception when unique_violation then
    raise notice 'PASS: a second live tag with the same number was rejected';
  end;

  -- A voided tag frees the number, which is the whole reason the index is partial.
  update titling.temporary_tag
     set status = 'void', void_reason = 'fixture', voided_at = now()
   where tenant_id = a_tenant and tag_number = 'TAG-0001';
  insert into titling.temporary_tag (tenant_id, tag_number, status)
  values (a_tenant, 'TAG-0001', 'assigned');
  raise notice 'PASS: the number is reusable once the prior tag is voided';

  -- A void without a reason is not a void.
  begin
    insert into titling.temporary_tag (tenant_id, tag_number, status)
    values (a_tenant, 'TAG-0002', 'void');
    failures := failures + 1;
    raise warning 'FAIL: a tag was voided with no reason and no timestamp';
  exception when check_violation then
    raise notice 'PASS: voiding without a reason was rejected';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 10. Tenant isolation reaches the new schemas ---';
  ---------------------------------------------------------------------------
  set local role mautodesk_app;
  perform set_config('app.tenant_id', b_tenant::text, true);

  select count(*) into n from lending.lender;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % of tenant A''s lenders', n;
  else raise notice 'PASS: tenant B sees none of tenant A''s lenders'; end if;

  select count(*) into n from titling.temporary_tag;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % of tenant A''s temporary tags', n;
  else raise notice 'PASS: tenant B sees none of tenant A''s temporary tags'; end if;

  select count(*) into n from compliance.compliance_run;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % of tenant A''s compliance runs', n;
  else raise notice 'PASS: tenant B sees none of tenant A''s compliance runs'; end if;

  -- Buy rates and reserve terms are the most commercially sensitive rows in the
  -- new schemas. Worth its own assertion rather than trusting the loop.
  select count(*) into n from lending.lender_program_version;
  if n <> 0 then failures := failures + 1;
    raise warning 'FAIL: tenant B saw % of tenant A''s lender program versions', n;
  else raise notice 'PASS: tenant B sees none of tenant A''s rate sheets'; end if;

  begin
    insert into lending.lender (tenant_id, code, name, type)
    values (a_tenant, 'FORGE', 'Forged', 'bank');
    failures := failures + 1;
    raise warning 'FAIL: tenant B forged a lender into tenant A';
  exception when insufficient_privilege or check_violation then
    raise notice 'PASS: cross-tenant lender insert rejected';
  end;

  ---------------------------------------------------------------------------
  raise notice '--- 11. The application cannot rewrite the law ---';
  ---------------------------------------------------------------------------
  begin
    insert into compliance.regulatory_rule (rule_code, jurisdiction_code, category, title)
    values ('ZZ-TEST-FORGED', 'ZZ-TEST', 'title', 'Rule the app wrote for itself');
    failures := failures + 1;
    raise warning 'FAIL: the application role authored a regulatory rule';
  exception when insufficient_privilege then
    raise notice 'PASS: the application role cannot write regulatory rules';
  end;

  ---------------------------------------------------------------------------
  if failures > 0 then
    raise exception 'V0010 INVARIANTS FAILED: % check(s)', failures;
  end if;
  raise notice '=== V0010 INVARIANTS PASSED ===';
end
$probe$;
