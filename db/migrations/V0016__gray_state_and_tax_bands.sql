--------------------------------------------------------------------------------
-- MautoDesk — V0016 the GRAY state, and a tax that comes in bands
--
-- Two engine changes need schema before they can be stored. Neither invents a
-- legal fact; both remove a way for the system to be confidently wrong.
--
-- ============================================================================
-- 1. GRAY — "we did not check this" is now a verdict
-- ============================================================================
--
-- Until now compliance_result.status allowed four values and every one of them
-- meant the rule had been evaluated. There was no way to say "this rule asks
-- about a fact nobody entered", so the evaluator said the next closest thing:
-- the condition did not match, the rule did not fire, no finding was produced,
-- and compliance_run.overall_status came back 'green'.
--
-- That is the most dangerous defect this system can have. It fails silently,
-- it fails in the direction of saying yes, and a half-filled deal is
-- indistinguishable from a lawful one. RuleEvaluator 2.0.0 makes conditions
-- three-valued, so an undecidable rule now produces status 'unknown' and the
-- run comes back 'gray'.
--
-- GREEN consequently means something narrower than it did yesterday: every
-- applicable rule was decided, on complete facts, and none raised a finding.
-- Runs already stored under engine_version 1.0.0 keep their recorded status,
-- which is correct — a run records what the engine of the day concluded, and
-- rewriting history to today's logic would destroy the only evidence of what
-- the system actually told somebody.
--
-- missing_facts is stored rather than recomputed because the whole value of a
-- GRAY verdict is that it names the fields to go and fill in. Recomputing it
-- later would need the facts of the day, which is precisely what was missing.
--
-- ============================================================================
-- 2. TAX BANDS — Oklahoma's used-vehicle excise tax cannot be a single rate
-- ============================================================================
--
-- 68 O.S. § 2103, archived by V0015 and quoted here from that file:
--
--   "for the year beginning July 1, 2002, and all subsequent years, Twenty
--    Dollars ($20.00) on the first One Thousand Five Hundred Dollars
--    ($1,500.00) or less of value of such vehicle, and three and one-fourth
--    percent (3 1/4%) of the remaining value of such vehicle"
--
-- A fixed amount on a first band plus a percentage on the remainder. The rule
-- set schema carried one 'stateRate' scalar, which cannot express it at all —
-- so the shape is extended to schema_version 2 with an ordered 'bands' array
-- and an explicit 'taxRounding'. The same section says:
--
--   "In computing the motor vehicle excise tax, the amount collected shall be
--    rounded to the nearest dollar."
--
-- which is why rounding is now per-rule-set rather than the engine's global
-- two-decimal default. Money stays numeric(14,2) everywhere; it is the tax
-- line that rounds differently, not the ledger.
--
-- THIS MIGRATION STILL ADDS NO APPROVED TAX NUMBER. It adds the shape, and a
-- DRAFT rule set carrying only what the two archived statutes actually say.
-- The nine fields no primary source has answered yet stay UNVERIFIED. See
-- docs/16-oklahoma-tax-research.md §3.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- 1a. Widen the two status checks.
--
-- The constraints were created inline and so carry PostgreSQL's generated
-- names. Looked up rather than assumed: a name guessed wrong here would either
-- fail loudly or, worse, drop a different constraint.
--------------------------------------------------------------------------------
do $$
declare
  name text;
  found int;
begin
  -- Matched on the column rather than on the constraint body. Scanning
  -- pg_get_constraintdef() text for '%status%' would also match the severity
  -- check, and SELECT INTO takes an arbitrary row rather than erroring on
  -- several - so a loose match here could silently drop the wrong constraint.
  select count(*), min(con.conname) into found, name
    from pg_constraint con
    join pg_class cls on cls.oid = con.conrelid
    join pg_namespace nsp on nsp.oid = cls.relnamespace
   where nsp.nspname = 'compliance'
     and cls.relname = 'compliance_run'
     and con.contype = 'c'
     and con.conkey = array[
       (select attnum from pg_attribute
         where attrelid = cls.oid and attname = 'overall_status')]::int2[];

  if found <> 1 then
    raise exception
      'Expected exactly one check constraint on '
      'compliance.compliance_run.overall_status, found %. V0010 created one; '
      'something has already altered it, and this migration will not guess '
      'which to replace.', found;
  end if;

  execute format(
    'alter table compliance.compliance_run drop constraint %I', name);
end
$$;

alter table compliance.compliance_run
  add constraint compliance_run_overall_status_ck
  check (overall_status in ('green', 'yellow', 'gray', 'red'));

do $$
declare
  name text;
  found int;
begin
  select count(*), min(con.conname) into found, name
    from pg_constraint con
    join pg_class cls on cls.oid = con.conrelid
    join pg_namespace nsp on nsp.oid = cls.relnamespace
   where nsp.nspname = 'compliance'
     and cls.relname = 'compliance_result'
     and con.contype = 'c'
     and con.conkey = array[
       (select attnum from pg_attribute
         where attrelid = cls.oid and attname = 'status')]::int2[];

  if found <> 1 then
    raise exception
      'Expected exactly one check constraint on '
      'compliance.compliance_result.status, found %.', found;
  end if;

  execute format(
    'alter table compliance.compliance_result drop constraint %I', name);
end
$$;

alter table compliance.compliance_result
  add constraint compliance_result_status_ck
  check (status in ('pass', 'warning', 'block', 'review', 'unknown'));

--------------------------------------------------------------------------------
-- 1b. Record what was missing, and how much of it there was.
--
-- Nullable and defaulted, so the currently deployed application keeps working
-- against this schema for the release in which it ships (expand/contract).
--------------------------------------------------------------------------------
alter table compliance.compliance_result
  add column if not exists missing_facts text[];

alter table compliance.compliance_run
  add column if not exists unknown_count int not null default 0;

comment on column compliance.compliance_result.missing_facts is
  'For status = ''unknown'': the fact paths the rule needed and the deal did '
  'not carry, e.g. {deal.odometer,customer.state}. Stored rather than derived, '
  'because the point of a GRAY verdict is to name the fields somebody must '
  'fill in, and the facts of the day are exactly what was absent.';

comment on column compliance.compliance_run.unknown_count is
  'How many rules could not be decided. Non-zero implies overall_status = '
  '''gray'' unless something blocking outranked it.';

--------------------------------------------------------------------------------
-- An unknown verdict must name what is unknown, and a decided one must not
-- claim anything is missing. Without this the column drifts into decoration.
--------------------------------------------------------------------------------
alter table compliance.compliance_result
  add constraint compliance_result_missing_facts_ck
  check (
    (status = 'unknown')
    or missing_facts is null
    or cardinality(missing_facts) = 0
  );

--------------------------------------------------------------------------------
-- A gray run must have an undecided rule behind it, and a run with an
-- undecided rule must not call itself green or yellow. Red outranks gray, so
-- a red run may legitimately carry unknowns.
--------------------------------------------------------------------------------
alter table compliance.compliance_run
  add constraint compliance_run_gray_ck
  check (
    (overall_status = 'gray') = (unknown_count > 0 and blocking_count = 0)
  );

--------------------------------------------------------------------------------
-- 2a. The rule-set shape is versioned, so say what version 2 means.
--------------------------------------------------------------------------------
comment on column sales.rule_set.schema_version is
  'The shape of the rules jsonb. 1: a single stateRate/localRate scalar pair. '
  '2: adds an ordered bands array for jurisdictions whose tax is not a flat '
  'percentage (Oklahoma levies $20 on the first $1,500 of value plus 3.25% of '
  'the remainder, 68 O.S. 2103) and taxRounding, because the same section '
  'rounds the excise tax to the nearest dollar while every other line on the '
  'deal rounds to the cent. The engine reads both shapes; a version-2 row may '
  'carry bands or a flat rate but never both, and the engine refuses the row '
  'rather than choosing.';

--------------------------------------------------------------------------------
-- 2b. Oklahoma, drafted from the two archived statutes and nothing else.
--
-- Every value below is quoted in the source_citation or left UNVERIFIED. The
-- nine open fields are open because no primary source has been retrieved for
-- them yet, not because they were forgotten:
--
--   the vehicle sales tax alongside the excise tax, local rate sourcing,
--   trade-in credit policy and cap, rebate treatment, doc fee cap and
--   taxability, title fee, registration/tag fees, maximum tax, accessories
--
-- approved_at is NULL, so the deal engine still refuses to price an Oklahoma
-- deal. That refusal is the feature. What changes is that the refusal is now
-- about the nine missing fields rather than about the engine being unable to
-- hold the two we have.
--------------------------------------------------------------------------------
insert into sales.rule_set
  (id, jurisdiction_id, schema_version, version, effective_from,
   rules, source_citation, source_url, notes)
select
  '0d000000-0000-4000-8000-000000000001',
  j.id,
  2,
  2,
  date '2025-07-01',
  jsonb_build_object(
    -- 68 O.S. § 2104(B): "The value of any vehicle ... shall be the actual
    -- sales price of such a vehicle."
    'taxBasis', 'actual_sales_price',

    -- 68 O.S. § 2103(A)(1)(c). Ordered, lowest band first. The last band is
    -- unbounded. 'flat' is charged whenever the base reaches the band at all,
    -- which is what "on the first $1,500.00 or less" says: a $900 car pays the
    -- full twenty dollars.
    'bands', jsonb_build_array(
      jsonb_build_object(
        'upTo', '1500.00',
        'flat', '20.00',
        'rate', '0',
        'note', 'Twenty Dollars ($20.00) on the first One Thousand Five Hundred '
                'Dollars ($1,500.00) or less of value of such vehicle'),
      jsonb_build_object(
        'upTo', null,
        'flat', null,
        'rate', '3.25',
        'note', 'and three and one-fourth percent (3 1/4%) of the remaining '
                'value of such vehicle')
    ),

    -- 68 O.S. § 2103: "the amount collected shall be rounded to the nearest
    -- dollar."
    'taxRounding', 'dollars',

    -- Not stated for ordinary vehicles. The off-road rate is expressly levied
    -- "before any discounts or credits are given for a trade-in", and an
    -- exclusion written into one rate is not a rule about the others. A reading
    -- either way is worth thousands of dollars on a deal, so it stays open.
    'tradeInCreditPolicy', 'UNVERIFIED',
    'tradeInCreditCap',    null,

    -- Each of these needs its own primary source. See docs/16 §3.
    'localRateSource', 'UNVERIFIED',
    'localRate',       null,
    'rebateTaxable',   null,
    'docFeeCap',       null,
    'docFeeTaxable',   null,
    'maximumTax',      null,
    'fees',            jsonb_build_array(),

    -- The ledger still rounds to the cent. Only the tax line rounds to the
    -- dollar, which is what taxRounding above is for.
    'rounding',      'AwayFromZero',
    'roundingScale', 2
  ),
  '68 O.S. § 2103 (excise tax and rounding) and § 2104 (value), as archived and '
  'hashed by V0015. DRAFT: covers the excise tax only. The vehicle sales tax, '
  'local sourcing, trade-in credit, rebate treatment, documentation-fee limits '
  'and title and registration fees are NOT in these two sections and are NOT in '
  'this rule set.',
  'https://www.oscn.net/applications/oscn/DeliverDocument.asp?CiteID=440208',
  'DRAFT, UNAPPROVED. Drafted by repository tooling from archived primary text; '
  'NOT a tax opinion and NOT reviewed by a CPA or tax counsel. Nine fields '
  'remain UNVERIFIED (docs/16-oklahoma-tax-research.md §3) and the engine will '
  'refuse to price a deal against this row for as long as approved_at is null. '
  'Approve with the rulekeeper CLI, which inserts a new version rather than '
  'editing this one.'
from sales.tax_jurisdiction j
where j.level = 'state' and j.state_code = 'OK'
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- 3. Say out loud what did and did not change.
--------------------------------------------------------------------------------
do $$
declare
  drafted int;
  approved int;
begin
  select count(*) into drafted
    from sales.rule_set where approved_at is null;

  select count(*) into approved
    from sales.rule_set where approved_at is not null;

  raise notice
    'GRAY state: compliance_result.status accepts ''unknown'', compliance_run '
    'accepts ''gray'', and missing_facts records which fields were absent. A '
    'rule over an uncaptured fact no longer reads as a pass.';

  raise notice
    'Tax bands: rule_set schema_version 2 adds banded rates and dollar '
    'rounding. Oklahoma drafted from 68 O.S. 2103/2104 with nine fields still '
    'UNVERIFIED. Rule sets: % unapproved, % approved. The deal engine prices '
    'nothing until a CPA or tax counsel approves one.', drafted, approved;
end
$$;
