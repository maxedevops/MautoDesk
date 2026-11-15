--------------------------------------------------------------------------------
-- MautoDesk — the approval register, and what the rule-keeper may do to it
--
-- V0017 creates compliance.pending_approval: the saved place for a reviewer's
-- disposition, so that engine work does not wait on procurement. The register's
-- whole value is that a question, once asked, still reads the way it did when
-- somebody answered it — which is a claim about triggers and grants, not about
-- intentions. So it is asserted here.
--
-- Run as the rule-keeper wherever the subject is what that role may do, and
-- self-rolling-back throughout: a test that leaves a resolved register item
-- behind would make the open-item count a measure of this file's leftovers.
--
-- Usage: psql -v ON_ERROR_STOP=1 -f db/tests/approval_register.sql
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- The register exists and is populated, with nothing disposed of.
--------------------------------------------------------------------------------
do $$
declare
  total int;
  resolved int;
  attorney int;
  tax int;
begin
  select count(*) into total from compliance.pending_approval;
  select count(*) into resolved
    from compliance.pending_approval where resolved_at is not null;
  select count(*) into attorney
    from compliance.pending_approval where reviewer_discipline = 'attorney';
  select count(*) into tax
    from compliance.pending_approval where reviewer_discipline = 'cpa_or_tax_counsel';

  if total = 0 then
    raise exception 'The approval register is empty. V0017 seeds it.'
      using errcode = 'check_violation';
  end if;

  if resolved > 0 then
    raise exception
      'The register has % resolved item(s) in a freshly migrated database. Nothing '
      'may be disposed of by a migration or a test.', resolved
      using errcode = 'check_violation';
  end if;

  if attorney = 0 or tax = 0 then
    raise exception
      'Expected items for both an attorney (%) and a CPA or tax counsel (%). '
      'Conflating the two disciplines is how a pack gets signed by somebody out '
      'of their depth.', attorney, tax
      using errcode = 'check_violation';
  end if;

  raise notice
    'PASS: % open items, none resolved (% for an attorney, % for tax).',
    total, attorney, tax;
end
$$;

--------------------------------------------------------------------------------
-- Every item names a target consistent with its kind, and asks a real question.
--------------------------------------------------------------------------------
do $$
declare
  bad int;
begin
  select count(*) into bad
    from compliance.pending_approval
   where (item_kind = 'rule_version' and rule_version_id is null)
      or (item_kind = 'tax_rule_set' and tax_rule_set_id is null)
      or length(btrim(question)) < 40;

  if bad > 0 then
    raise exception
      '% register item(s) either point at nothing or ask nothing.', bad
      using errcode = 'check_violation';
  end if;

  raise notice 'PASS: every item points at its subject and states a question.';
end
$$;

--------------------------------------------------------------------------------
-- Nothing in the register is approved. The register records that something is
-- waiting; only an approved version row activates anything, and this is the
-- assertion that keeps those two ideas apart.
--------------------------------------------------------------------------------
do $$
declare
  active int;
begin
  select count(*) into active
    from compliance.pending_approval p
    join compliance.regulatory_rule_version v on v.id = p.rule_version_id
   where v.approved_at is not null;

  if active > 0 then
    raise exception
      '% register item(s) point at an APPROVED rule version. A pending item is '
      'by definition waiting; if the rule is already approved, the item should '
      'have been resolved.', active
      using errcode = 'check_violation';
  end if;

  select count(*) into active
    from compliance.pending_approval p
    join sales.rule_set s on s.id = p.tax_rule_set_id
   where s.approved_at is not null;

  if active > 0 then
    raise exception
      '% register item(s) point at an APPROVED tax rule set.', active
      using errcode = 'check_violation';
  end if;

  raise notice 'PASS: nothing in the register points at something already approved.';
end
$$;

--------------------------------------------------------------------------------
-- The trigger: a question, once asked, cannot be rewritten.
--
-- This is the one that matters. A register whose questions can be edited after
-- the fact records nothing useful about what a reviewer actually agreed to.
--------------------------------------------------------------------------------
do $$
declare
  item uuid;
  rewritten boolean := false;
begin
  select id into item from compliance.pending_approval
   where resolved_at is null order by sort_order limit 1;

  begin
    update compliance.pending_approval
       set question = 'Something quite different, asked after the fact.'
     where id = item;
    rewritten := true;
  exception
    when restrict_violation then
      raise notice
        'PASS: the question cannot be rewritten (%).', replace(sqlerrm, E'\n', ' ');
  end;

  if rewritten then
    raise exception
      'A register question was rewritten. The question as asked is part of the '
      'record of the answer.'
      using errcode = 'check_violation';
  end if;

  raise exception 'ROLLBACK_PROBE'
    using errcode = 'check_violation',
          hint = 'expected: the probe rolls itself back';
exception
  when check_violation then
    if sqlerrm = 'ROLLBACK_PROBE' then
      raise notice 'Question-rewrite probe rolled back.';
    else
      raise;
    end if;
end
$$;

--------------------------------------------------------------------------------
-- A disposition is all-or-nothing, and permanent once made.
--------------------------------------------------------------------------------
do $$
declare
  item uuid;
  half_filled boolean := false;
  edited_twice boolean := false;
begin
  select id into item from compliance.pending_approval
   where resolved_at is null order by sort_order limit 1;

  -- A date with no name and no note is the half-recorded audit entry this
  -- system exists to avoid.
  begin
    update compliance.pending_approval set resolved_at = now() where id = item;
    half_filled := true;
  exception
    when check_violation then
      raise notice 'PASS: a resolution without a name or a note is refused.';
  end;

  if half_filled then
    raise exception 'A register item was half-resolved.'
      using errcode = 'check_violation';
  end if;

  -- A complete disposition is accepted exactly once.
  update compliance.pending_approval
     set resolved_at = now(),
         resolved_by = 'Dale Cooper',
         resolution = 'approved',
         resolution_note =
           'Reviewed against the archived primary text and approved as drafted, '
           'subject to nothing. This note exists only to satisfy the forty '
           'character floor in a test.'
   where id = item;

  raise notice 'PASS: a complete disposition is accepted.';

  -- And cannot then be revised.
  begin
    update compliance.pending_approval
       set resolution = 'rejected',
           resolution_note =
             'Changed my mind, which is exactly what must not be possible here.'
     where id = item;
    edited_twice := true;
  exception
    when restrict_violation then
      raise notice
        'PASS: a resolved item cannot be re-resolved (%).', replace(sqlerrm, E'\n', ' ');
  end;

  if edited_twice then
    raise exception
      'A disposition was revised. An approval is a permanent record; a '
      'correction is a new item, not an edit.'
      using errcode = 'check_violation';
  end if;

  -- Nor deleted.
  begin
    delete from compliance.pending_approval where id = item;

    raise exception 'A register item was deleted.'
      using errcode = 'check_violation';
  exception
    when others then
      if sqlerrm like 'A register item was deleted.%' then
        raise;
      end if;

      raise notice
        'PASS: a register item cannot be deleted (%).', replace(sqlerrm, E'\n', ' ');
  end;

  raise exception 'ROLLBACK_PROBE'
    using errcode = 'check_violation',
          hint = 'expected: the probe rolls itself back';
exception
  when check_violation then
    if sqlerrm = 'ROLLBACK_PROBE' then
      raise notice 'Disposition probe rolled back.';
    else
      raise;
    end if;
end
$$;

--------------------------------------------------------------------------------
-- What the rule-keeper may and may not do here.
--
-- It closes register items as a side effect of the approvals it writes, so it
-- needs UPDATE on the resolution columns — and must not be able to touch
-- anything else, insert an item, or delete one.
--------------------------------------------------------------------------------
do $$
declare
  item uuid;
  wrote boolean := false;
begin
  select id into item from compliance.pending_approval
   where resolved_at is null order by sort_order limit 1;

  set local role mautodesk_rulekeeper;

  -- It can read the register. Walking this table is the point of the role.
  perform count(*) from compliance.pending_approval;
  raise notice 'PASS: the rule-keeper can read the register.';

  -- It can resolve an item.
  update compliance.pending_approval
     set resolved_at = now(),
         resolved_by = 'Dale Cooper',
         resolution = 'approved',
         resolution_note =
           'Approved as drafted against the archived text. Forty characters of '
           'note, as the constraint requires.'
   where id = item;
  raise notice 'PASS: the rule-keeper can resolve a register item.';

  -- It cannot invent an item to resolve.
  begin
    insert into compliance.pending_approval
      (item_kind, jurisdiction_code, question, reviewer_discipline)
    values
      ('statutory_question', 'US-OK',
       'A question this role asked itself, which is not how a register works.',
       'attorney');
    wrote := true;
  exception
    when insufficient_privilege then
      raise notice 'PASS: the rule-keeper cannot add a register item.';
  end;

  if wrote then
    raise exception
      'The rule-keeper inserted a register item. Questions come from migrations, '
      'under review; a role that can both ask and answer is a role that can '
      'approve its own work.'
      using errcode = 'check_violation';
  end if;

  reset role;

  raise exception 'ROLLBACK_PROBE'
    using errcode = 'check_violation',
          hint = 'expected: the probe rolls itself back';
exception
  when check_violation then
    reset role;

    if sqlerrm = 'ROLLBACK_PROBE' then
      raise notice 'Rule-keeper register probe rolled back.';
    else
      raise;
    end if;
end
$$;

--------------------------------------------------------------------------------
-- The rule-keeper can now approve a tax rule set, and still nothing else in
-- sales. V0017 grants it INSERT on sales.rule_set so that taxkeeper has an
-- identity to run as; this is the assertion that the grant is that narrow.
--------------------------------------------------------------------------------
do $$
declare
  jurisdiction uuid;
  wrote boolean := false;
begin
  select jurisdiction_id into jurisdiction
    from sales.rule_set where approved_at is null limit 1;

  set local role mautodesk_rulekeeper;

  -- It can insert an approved rule set. This is the whole point.
  insert into sales.rule_set
    (id, jurisdiction_id, schema_version, version, effective_from, rules,
     source_citation, approved_at, approved_by)
  values
    ('dddddddd-0000-4000-8000-000000000002', jurisdiction, 2, 9001,
     date '2099-01-01',
     '{"taxBasis":"actual_sales_price","tradeInCreditPolicy":"none"}'::jsonb,
     'Probe row from db/tests/approval_register.sql, rolled back',
     now(), 'Dale Cooper');
  raise notice 'PASS: the rule-keeper can insert an approved tax rule set.';

  -- It cannot edit one. sales.rule_set is append-only as of V0017.
  begin
    update sales.rule_set set approved_by = 'somebody else'
     where id = 'dddddddd-0000-4000-8000-000000000002';
    wrote := true;
  exception
    when others then
      raise notice
        'PASS: a tax rule set cannot be edited (%).', replace(sqlerrm, E'\n', ' ');
  end;

  if wrote then
    raise exception
      'A tax rule set was edited. A figure that priced a signed contract cannot '
      'be revised by editing the row that produced it.'
      using errcode = 'check_violation';
  end if;

  -- And it still cannot read a deal.
  begin
    perform count(*) from sales.deal;

    raise exception 'The rule-keeper can read sales.deal.'
      using errcode = 'check_violation';
  exception
    when insufficient_privilege then
      raise notice 'PASS: sales.deal is still not readable.';
  end;

  reset role;

  raise exception 'ROLLBACK_PROBE'
    using errcode = 'check_violation',
          hint = 'expected: the probe rolls itself back';
exception
  when check_violation then
    reset role;

    if sqlerrm = 'ROLLBACK_PROBE' then
      raise notice 'Tax rule set probe rolled back.';
    else
      raise;
    end if;
end
$$;

--------------------------------------------------------------------------------
-- Prove the rollbacks happened. A probe row that survived would leave an
-- APPROVED tax rule set in the database, which the deal engine would then
-- happily price deals against.
--------------------------------------------------------------------------------
do $$
declare
  resolved int;
begin
  if exists (select 1 from sales.rule_set
              where id = 'dddddddd-0000-4000-8000-000000000002') then
    raise exception
      'The probe left an APPROVED tax rule set behind. The deal engine would '
      'price Oklahoma deals against it.'
      using errcode = 'check_violation';
  end if;

  select count(*) into resolved
    from compliance.pending_approval where resolved_at is not null;

  if resolved > 0 then
    raise exception
      'The probe left % resolved register item(s) behind.', resolved
      using errcode = 'check_violation';
  end if;

  raise notice 'PASS: no probe row persists; the register is still fully open.';
end
$$;

\echo 'Approval register verified: questions are permanent, dispositions are once-only.'
