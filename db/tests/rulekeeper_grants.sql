--------------------------------------------------------------------------------
-- The rule-keeper can approve a rule and do nothing else.
--
-- V0014 grants a role the right to insert the law. That is the most
-- security-sensitive privilege in this database, so its boundaries are asserted
-- rather than described: what it may do, what it may not, and — the one that
-- would fail silently — that the audit entry it writes actually links to the
-- previous one.
--
-- Run: psql -U postgres -d mautodesk -v ON_ERROR_STOP=1 -f rulekeeper_grants.sql
--------------------------------------------------------------------------------

\set ON_ERROR_STOP on

do $$
declare
  failures  int := 0;
  rule_id   uuid := '0a000000-0000-4000-8000-000000000001';  -- OK-PREREG-001
  src_id    uuid := '05100000-0000-4000-8000-000000000002';
  probe_id  uuid := 'dddddddd-0000-4000-8000-000000000001';
  v_prev    bytea;
  v_new     bytea;
  link_ok   boolean;
begin
  ---------------------------------------------------------------------------
  -- 0. The role exists, can log in, and cannot bypass RLS.
  ---------------------------------------------------------------------------
  if not exists (select 1 from pg_roles
                  where rolname = 'mautodesk_rulekeeper' and rolcanlogin) then
    raise warning 'FAIL: mautodesk_rulekeeper does not exist or cannot log in';
    failures := failures + 1;
  elsif exists (select 1 from pg_roles
                 where rolname = 'mautodesk_rulekeeper'
                   and (rolsuper or rolbypassrls)) then
    raise warning 'FAIL: mautodesk_rulekeeper can bypass row level security';
    failures := failures + 1;
  else
    raise notice 'PASS: the role exists, logs in, and cannot bypass RLS';
  end if;

  ---------------------------------------------------------------------------
  -- 1. It may insert an approved rule version. This is the whole point, and a
  --    test that only proves the denials would pass on a role that can do
  --    nothing at all.
  ---------------------------------------------------------------------------
  set local role mautodesk_rulekeeper;

  begin
    insert into compliance.regulatory_rule_version
      (id, rule_id, version, status, effective_from, effective_to,
       source_version_id, trigger_event, definition, severity,
       approved_at, approved_by, approval_note)
    values
      (probe_id, rule_id, '0.0.1-grant-probe', 'approved',
       date '1900-01-01', date '1900-02-01',
       src_id, 'DEAL_SOLD',
       '{"conditions":{"all":[]},"actions":[]}'::jsonb, 'informational',
       now(), 'Grant probe (db/tests/rulekeeper_grants.sql)',
       'Inserted by a test to prove the grant works. Effective in 1900 so it '
       'governs nothing, and rolled back before this transaction ends.');
    raise notice 'PASS: the role can insert an approved rule version';
  exception when insufficient_privilege then
    raise warning 'FAIL: the role cannot insert a rule version, so it cannot approve anything';
    failures := failures + 1;
  end;

  ---------------------------------------------------------------------------
  -- 2. It may NOT update or delete one. An approval is a new row; a role that
  --    can edit history can rewrite what the system believed on a past deal.
  ---------------------------------------------------------------------------
  begin
    update compliance.regulatory_rule_version
       set approved_by = 'someone else' where id = probe_id;
    raise warning 'FAIL: the role updated a rule version';
    failures := failures + 1;
  exception
    when insufficient_privilege then
      raise notice 'PASS: UPDATE on a rule version is refused by privilege';
    when others then
      -- app.deny_mutation() raises insufficient_privilege too; either refusal
      -- is the right outcome, and which one fires first does not matter.
      raise notice 'PASS: UPDATE on a rule version is refused (%)', sqlerrm;
  end;

  begin
    delete from compliance.regulatory_rule_version where id = probe_id;
    raise warning 'FAIL: the role deleted a rule version';
    failures := failures + 1;
  exception when others then
    raise notice 'PASS: DELETE on a rule version is refused (%)', sqlerrm;
  end;

  ---------------------------------------------------------------------------
  -- 3. It may NOT author the law — only approve a draft somebody reviewed.
  ---------------------------------------------------------------------------
  begin
    insert into compliance.regulatory_rule (rule_code, jurisdiction_code, category, title)
    values ('ZZ-GRANT-PROBE-001', 'US-OK', 'other', 'Should never be insertable');
    raise warning 'FAIL: the role invented a new rule';
    failures := failures + 1;
  exception when insufficient_privilege then
    raise notice 'PASS: inserting a new rule is refused';
  end;

  begin
    insert into compliance.regulatory_source (authority_id, source_type, title, citation)
    select id, 'statute', 'Should never be insertable', 'ZZ-GRANT-PROBE'
      from compliance.regulatory_authority limit 1;
    raise warning 'FAIL: the role invented a new source';
    failures := failures + 1;
  exception when insufficient_privilege then
    raise notice 'PASS: inserting a new source is refused';
  end;

  ---------------------------------------------------------------------------
  -- 4. It cannot see tenant data. A credential that can write the law should
  --    not also be a window into the dealerships being judged by it.
  ---------------------------------------------------------------------------
  begin
    perform 1 from sales.deal limit 1;
    raise warning 'FAIL: the role can read sales.deal';
    failures := failures + 1;
  exception when insufficient_privilege then
    raise notice 'PASS: sales.deal is not readable';
  end;

  begin
    perform 1 from inventory.vehicle limit 1;
    raise warning 'FAIL: the role can read inventory.vehicle';
    failures := failures + 1;
  exception when insufficient_privilege then
    raise notice 'PASS: inventory.vehicle is not readable';
  end;

  ---------------------------------------------------------------------------
  -- 5. The audit entry links to its predecessor.
  --
  --    This is the assertion that exists because of a defect rather than a
  --    guess. audit.chain_event() reads the previous row as the calling role;
  --    with INSERT but no SELECT the read returns nothing instead of failing,
  --    prev_hash comes out NULL, and the row claims to start the chain. The
  --    ledger still looks like evidence and no longer is.
  ---------------------------------------------------------------------------
  insert into audit.event
    (tenant_id, actor_type, actor_display, access_reason, action,
     entity_schema, entity_type, entity_id, metadata)
  values
    (null, 'platform_admin', 'Grant probe', 'Proving the chain links',
     'compliance', 'regulatory_rule_version', probe_id,
     '{"probe":true}'::jsonb)
  returning hash into v_prev;

  insert into audit.event
    (tenant_id, actor_type, actor_display, access_reason, action,
     entity_schema, entity_type, entity_id, metadata)
  values
    (null, 'platform_admin', 'Grant probe', 'Proving the chain links',
     'compliance', 'regulatory_rule_version', probe_id,
     '{"probe":true,"second":true}'::jsonb)
  returning hash into v_new;

  select e.prev_hash is not null and e.prev_hash = v_prev into link_ok
    from audit.event e where e.hash = v_new;

  if coalesce(link_ok, false) then
    raise notice 'PASS: a second platform audit event links to the first';
  else
    raise warning
      'FAIL: the audit entry did not link to its predecessor — prev_hash is null '
      'or wrong, so audit.verify_chain(null) will report this chain broken';
    failures := failures + 1;
  end if;

  ---------------------------------------------------------------------------
  -- 6. It may not write any other kind of audit event, and may not write one
  --    without a reason. The policy says so; this proves the policy works.
  ---------------------------------------------------------------------------
  begin
    insert into audit.event (tenant_id, actor_type, access_reason, action)
    values (null, 'platform_admin', 'Trying something else', 'identity.user.deleted');
    raise warning 'FAIL: the role wrote an audit event for an unrelated action';
    failures := failures + 1;
  exception when others then
    raise notice 'PASS: an unrelated audit action is refused (%)', sqlerrm;
  end;

  begin
    insert into audit.event (tenant_id, actor_type, action)
    values (null, 'platform_admin', 'compliance.rule_version.approved');
    raise warning 'FAIL: the role wrote a platform audit event with no access_reason';
    failures := failures + 1;
  exception when others then
    raise notice 'PASS: a platform event with no access_reason is refused (%)', sqlerrm;
  end;

  reset role;

  if failures > 0 then
    raise exception '% rule-keeper grant assertion(s) failed', failures
      using errcode = 'check_violation';
  end if;

  raise notice 'Rule-keeper grants verified: it can approve, and it can do nothing else.';

  ---------------------------------------------------------------------------
  -- Nothing above is meant to persist: the probe row is an approved rule, and
  -- an approved rule is exactly what no test should leave behind. Both the rule
  -- version and the audit events are append-only, so they cannot be deleted —
  -- which is why this whole block raises at the end to roll itself back.
  ---------------------------------------------------------------------------
  raise exception 'ROLLBACK_PROBE'
    using errcode = 'check_violation',
          hint = 'expected: the probe rolls itself back';
exception
  when check_violation then
    if sqlerrm = 'ROLLBACK_PROBE' then
      raise notice 'Probe rows rolled back; no approved rule or audit entry persists.';
    else
      raise;
    end if;
end
$$;

--------------------------------------------------------------------------------
-- Prove the rollback actually happened. If the probe row survived, a test just
-- left an approved rule in the database and the CI gate that counts approvals
-- would be measuring this file's leftovers.
--------------------------------------------------------------------------------
do $$
begin
  if exists (select 1 from compliance.regulatory_rule_version
              where id = 'dddddddd-0000-4000-8000-000000000001') then
    raise exception
      'The grant probe left an approved rule version behind'
      using errcode = 'check_violation';
  end if;

  raise notice 'PASS: no probe row persists.';
end
$$;
