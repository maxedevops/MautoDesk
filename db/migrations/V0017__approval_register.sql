--------------------------------------------------------------------------------
-- MautoDesk — V0017 the approval register: a saved place for a signature
--
-- WHAT THIS IS FOR
--
-- Engine work must not wait on procurement. An Oklahoma attorney and a CPA are
-- both still to be retained, and everything they will eventually sign already
-- exists as drafted, source-cited rows. What did not exist was a durable record
-- of *what is waiting on whom* — that lived in prose in docs/15 and docs/16,
-- which is to say it lived nowhere a tool could read.
--
-- So this migration creates the slot and fills it. Every item a reviewer must
-- dispose of is a row here, with the question stated, the discipline required,
-- and an empty place for the approval that will eventually point back at it.
-- When counsel is retained, the work is to walk this table, not to re-read three
-- documents and reconstruct the list.
--
-- WHAT IT IS NOT
--
-- It is not an approval, and it cannot become one. Nothing in this table is read
-- by either engine. A row here does not make a draft usable; it records that the
-- draft is waiting. The only thing that activates a rule is a new, approved
-- version row inserted by the rulekeeper CLI, and that is unchanged.
--
-- It also confers no authority. 'reviewer_discipline' says what kind of
-- professional the item needs; it does not say anybody has been engaged, and the
-- register deliberately has no column in which somebody could record their own
-- approval of their own draft.
--
-- SHARED REFERENCE DATA
--
-- No tenant_id. What is awaiting legal review is a fact about the law, not about
-- a dealership, exactly as compliance.regulatory_rule_version and sales.rule_set
-- are. Registered in app.rls_exempt_table with that reasoning.
--------------------------------------------------------------------------------

create table compliance.pending_approval (
  id                    uuid primary key default gen_random_uuid(),

  -- What is waiting. Exactly one of the two target columns is set; the check
  -- below enforces it. Two nullable foreign keys rather than a polymorphic
  -- (table_name, row_id) pair, because a real foreign key cannot rot.
  rule_version_id       uuid references compliance.regulatory_rule_version(id),
  tax_rule_set_id       uuid references sales.rule_set(id),

  item_kind             text not null
                        check (item_kind in ('rule_version', 'tax_rule_set',
                                             'interpretation', 'statutory_question',
                                             'source_currency')),

  jurisdiction_code     text not null
                        references compliance.regulatory_jurisdiction(code),

  -- What the reviewer is being asked. Long-form on purpose: a question a
  -- reviewer cannot answer from this column alone is not yet a question.
  question              text not null,
  constraint pending_approval_question_ck check (length(btrim(question)) >= 40),

  -- Where the drafter got to, and why they stopped. This is the honest part:
  -- 'we read this sentence two ways and will not choose' belongs in writing.
  drafter_reading       text,

  -- Which profession the item needs. A compliance attorney cannot approve an
  -- excise-tax basis and a CPA cannot approve a titling deadline; conflating
  -- them is how a pack gets signed by somebody out of their depth.
  reviewer_discipline   text not null
                        check (reviewer_discipline in ('attorney', 'cpa_or_tax_counsel',
                                                       'either', 'project_owner')),

  -- Cited so a reviewer can start from the text rather than from our summary.
  source_version_id     uuid references compliance.regulatory_source_version(id),
  reference_document    text,

  blocks_go_live        boolean not null default true,
  sort_order            int not null default 0,

  -- The slot itself. Filled in only when the item is disposed of, and never by
  -- the same act that created the row.
  resolved_at           timestamptz,
  resolved_by           text,
  resolution            text check (resolution in ('approved', 'approved_with_change',
                                                   'rejected', 'withdrawn', 'answered')),
  resolution_note       text,

  -- Where the approval landed, for an item that produced one. Points at the NEW
  -- version row the rulekeeper inserted, never at the draft.
  approved_version_id   uuid references compliance.regulatory_rule_version(id),
  approved_rule_set_id  uuid references sales.rule_set(id),

  created_at            timestamptz not null default now(),

  -- Exactly one target, and it has to agree with item_kind.
  constraint pending_approval_target_ck check (
    case item_kind
      when 'rule_version'  then rule_version_id is not null and tax_rule_set_id is null
      when 'tax_rule_set'  then tax_rule_set_id is not null and rule_version_id is null
      else true
    end
  ),

  -- A resolution is all-or-nothing. A row with a date and no name, or a name and
  -- no disposition, is the half-filled audit record this system exists to avoid.
  constraint pending_approval_resolution_ck check (
    (resolved_at is null and resolved_by is null and resolution is null)
    or (resolved_at is not null
        and resolved_by is not null and length(btrim(resolved_by)) >= 3
        and resolution is not null
        and resolution_note is not null and length(btrim(resolution_note)) >= 40)
  ),

  -- Nothing may claim an approval landed somewhere without saying it was
  -- approved at all.
  constraint pending_approval_landing_ck check (
    (approved_version_id is null and approved_rule_set_id is null)
    or resolution in ('approved', 'approved_with_change')
  )
);

create index pending_approval_open_ix
  on compliance.pending_approval (jurisdiction_code, reviewer_discipline, sort_order)
  where resolved_at is null;

create index pending_approval_rule_ix
  on compliance.pending_approval (rule_version_id) where rule_version_id is not null;

--------------------------------------------------------------------------------
-- Append-only, like every other record of who decided what. A disposition is
-- the most consequential thing a human writes into this system; it is not an
-- editable field.
--
-- Rows start open and are closed exactly once, so UPDATE cannot be banned
-- outright the way it is on regulatory_rule_version. Instead the trigger allows
-- precisely one transition: open -> resolved. Nothing else about the row may
-- move, and a resolved row is frozen.
--------------------------------------------------------------------------------
create or replace function compliance.close_pending_approval_only()
returns trigger
language plpgsql
as $$
begin
  if old.resolved_at is not null then
    raise exception
      'compliance.pending_approval % is already resolved (% by %). A disposition '
      'is a permanent record. If it was wrong, resolve a new row; do not edit '
      'this one.', old.id, old.resolution, old.resolved_by
      using errcode = 'restrict_violation';
  end if;

  if new.resolved_at is null then
    raise exception
      'The only permitted update to compliance.pending_approval % is to resolve '
      'it. Nothing else about a pending item may be edited, because the question '
      'a reviewer answered must still read the way it did when they answered '
      'it.', old.id
      using errcode = 'restrict_violation';
  end if;

  -- Everything that is not part of closing the row must be untouched. Listed
  -- explicitly rather than compared with to_jsonb(), so that adding a column
  -- later forces a decision about which side of this line it falls on.
  if (new.id, new.rule_version_id, new.tax_rule_set_id, new.item_kind,
      new.jurisdiction_code, new.question, new.drafter_reading,
      new.reviewer_discipline, new.source_version_id, new.reference_document,
      new.blocks_go_live, new.created_at)
     is distinct from
     (old.id, old.rule_version_id, old.tax_rule_set_id, old.item_kind,
      old.jurisdiction_code, old.question, old.drafter_reading,
      old.reviewer_discipline, old.source_version_id, old.reference_document,
      old.blocks_go_live, old.created_at) then
    raise exception
      'Resolving compliance.pending_approval % may set the resolution columns and '
      'nothing else. The question as asked is part of the record of the answer.',
      old.id
      using errcode = 'restrict_violation';
  end if;

  return new;
end
$$;

comment on function compliance.close_pending_approval_only() is
  'Allows exactly one transition on compliance.pending_approval: open to '
  'resolved. Blocks every other update and all deletes, so a disposition and the '
  'question it answered are both permanent.';

create trigger pending_approval_close_only before update on compliance.pending_approval
  for each row execute function compliance.close_pending_approval_only();

create trigger pending_approval_no_delete before delete on compliance.pending_approval
  for each row execute function app.deny_mutation();

insert into app.rls_exempt_table (schema_name, table_name, reason) values
  ('compliance', 'pending_approval',
   'The register of what is awaiting legal or tax review. A fact about the law '
   'and its review status, not about any dealership, exactly like '
   'regulatory_rule_version and sales.rule_set.')
on conflict (schema_name, table_name) do nothing;

comment on table compliance.pending_approval is
  'Everything a human reviewer must dispose of before a jurisdiction can go '
  'live, with the question stated and an empty slot for the answer. Read by '
  'people and by the rulekeeper CLI; read by neither engine. A row here does '
  'NOT make a draft usable - only an approved version row does that.';

--------------------------------------------------------------------------------
-- The rule-keeper can read and close the register, and can now also approve a
-- tax rule set. Same shape as V0014: one table, one verb, nothing else.
--------------------------------------------------------------------------------
grant usage on schema sales to mautodesk_rulekeeper;

grant select on
  sales.rule_set,
  sales.tax_jurisdiction,
  compliance.pending_approval
  to mautodesk_rulekeeper;

grant insert on sales.rule_set to mautodesk_rulekeeper;

-- UPDATE, not INSERT: the register's rows are created by migration, and the
-- tool closes them. The trigger above constrains the update to the resolution
-- columns, so this grant cannot be used to rewrite a question.
grant update (resolved_at, resolved_by, resolution, resolution_note,
              approved_version_id, approved_rule_set_id)
  on compliance.pending_approval to mautodesk_rulekeeper;


--------------------------------------------------------------------------------
-- The audit policy has to learn two more verbs.
--
-- V0014 wrote the rule-keeper an INSERT policy on audit.event pinned to exactly
-- one action, 'compliance.rule_version.approved'. That was right at the time and
-- is the reason db/tests/rulekeeper_grants.sql can assert that an unrelated
-- action is refused.
--
-- The same identity now has two more things it can legitimately record: the
-- approval of a tax rule set, and the closing of a register item. Both are
-- platform-scope acts by the same operator against the same law tables, so they
-- belong to the same role and the same policy - but the policy must name them,
-- or the write fails with a row-level-security error that says nothing useful
-- about why.
--
-- Still an allow-list of exactly three strings. Widening it to a prefix match
-- like 'compliance.%' would let this role narrate anything it liked into the
-- ledger, which is precisely what the pin exists to prevent.
--------------------------------------------------------------------------------
drop policy if exists event_rulekeeper_append on audit.event;

create policy event_rulekeeper_append on audit.event
  for insert to mautodesk_rulekeeper
  with check (
    tenant_id is null
    and actor_type = 'platform_admin'
    and action in (
      'compliance.rule_version.approved',
      'sales.rule_set.approved',
      'compliance.pending_approval.resolved')
    and access_reason is not null
    and btrim(access_reason) <> ''
  );

comment on policy event_rulekeeper_append on audit.event is
  'The rule-keeper may append platform-scope events for exactly three acts: '
  'approving a regulatory rule version, approving a tax rule set, and resolving '
  'a register item. Every one of them must carry an access_reason. An allow-list '
  'rather than a prefix, so this identity cannot narrate arbitrary claims into '
  'the ledger.';

--------------------------------------------------------------------------------
-- sales.rule_set becomes append-only too.
--
-- V0014 made the compliance side permanent and left this side editable, which
-- was an inconsistency rather than a decision: a tax rule set that priced a
-- signed retail contract is evidence of what the dealer was told, and a figure
-- on a contract cannot be revised by editing the row that produced it.
--
-- Safe to add now: mautodesk_app has only SELECT (V0006), the V0007 seed is
-- insert-only with a NOT EXISTS guard, and V0016's draft uses ON CONFLICT DO
-- NOTHING. Nothing in the system updates this table.
--------------------------------------------------------------------------------
create trigger rule_set_immutable before update or delete on sales.rule_set
  for each row execute function app.deny_mutation();

comment on table sales.rule_set is
  'Versioned, effective-dated, source-cited tax and fee rules. Append-only as '
  'of V0017: an approval is a NEW version row carrying approved_at and '
  'approved_by, never an edit to an existing one. The deal engine loads only '
  'approved rows, so an unapproved row is inert.';

--------------------------------------------------------------------------------
-- SEEDING THE REGISTER
--
-- Keyed by rule code and version through a join rather than by literal uuid, so
-- that a draft which is missing or renamed makes this migration fail instead of
-- quietly registering a null. The counts asserted at the end are the check that
-- it did what it says.
--------------------------------------------------------------------------------

-- 1. The ten drafted rule versions. Each needs a disposition: approve as
--    drafted, approve with a stated change, or reject.
insert into compliance.pending_approval
  (item_kind, rule_version_id, jurisdiction_code, question, drafter_reading,
   reviewer_discipline, source_version_id, reference_document, sort_order)
select
  'rule_version',
  v.id,
  r.jurisdiction_code,
  'Does ' || r.rule_code || ' ' || v.version || ' state the requirement correctly for '
    || 'Oklahoma, as of its effective date? Approve as drafted, approve with a '
    || 'stated change, or reject. The rule is written out in plain English with '
    || 'its quoted source in docs/13-oklahoma-counsel-review.md.',
  v.approval_note,
  'attorney',
  v.source_version_id,
  'docs/13-oklahoma-counsel-review.md',
  100 + row_number() over (order by r.rule_code, v.version)
from compliance.regulatory_rule_version v
join compliance.regulatory_rule r on r.id = v.rule_id
where r.jurisdiction_code = 'US-OK'
  and v.approved_at is null
  and not exists (
    select 1 from compliance.pending_approval p where p.rule_version_id = v.id);

-- 2. The seven readings a developer must not settle. Six carry the marker
--    INTERPRETATION TO CONFIRM in an approval_note; the seventh is the VIN
--    inspection question left open in V0012's own header.
insert into compliance.pending_approval
  (item_kind, rule_version_id, jurisdiction_code, question, drafter_reading,
   reviewer_discipline, reference_document, sort_order)
select 'interpretation', v.id, 'US-OK', q.question, q.reading, 'attorney',
       'docs/13-oklahoma-counsel-review.md', 200 + q.ord
from (values
  ('OK-PREREG-001', 1,
   'Does Oklahoma pre-registration apply to a wholesale deal? The archived source '
   'says the process "applies to all vehicles (new or used) purchased in the state '
   'of Oklahoma" with no wholesale carve-out, and the rule as drafted restricts '
   'itself to retail and consignment. Is that narrowing correct?',
   'Drafted narrow. The narrowing is a reading, not something the text states.'),

  ('OK-PLATE-TRANSFER-001', 2,
   'Should a transferred plate without a decal block the deal or merely warn? The '
   'text says the dealer "should" rather than stating a prohibition, so the rule is '
   'drafted as a warning. Is warning the right severity, and should members of '
   'tribal nations other than the Chickasaw and Choctaw Nations be modelled?',
   'Drafted as a warning on the strength of "should". Tribal nations other than '
   'the Chickasaw and Choctaw are not modelled at all.'),

  ('OK-TITLE-VIN-INSPECTION-001', 3,
   'Should an outstanding VIN inspection stop the dealership from contracting, or '
   'only from requesting the title? The rule as drafted blocks the title request '
   'alone. This is the question V0012 left open in its header rather than in an '
   'approval note.',
   'Drafted to block the title request only, not contracting.'),

  ('OK-TEMP-TAG-FLEET-001', 4,
   'Is the ninety-day fleet plate mandatory when the attestation and fleet-number '
   'conditions are met, or is it an election the purchaser may make? The rule is '
   'drafted as mandatory. If it is an election, the severity is wrong and possibly '
   'the shape of the rule is too.',
   'Drafted as mandatory, treating the attestation and fleet number as conditions.'),

  ('OK-TEMP-TAG-DEALER-60-001', 5,
   'Does a commercial fleet vehicle that does NOT meet the attestation and '
   'fleet-number conditions fall back to the sixty-day dealer plate? The rule as '
   'drafted excludes fleet vehicles entirely, on the reading that the fleet '
   'subdivision is the alternative to this one. If there is a fallback, that '
   'exclusion is wrong.',
   'Drafted to exclude commercial fleet vehicles entirely.'),

  ('OK-REG-DEADLINE-001', 6,
   'Should a dealer-facing system raise the new owner registration deadline at '
   'all? The duty is the owner rather than the dealership, so the rule is '
   'drafted as a warning that surfaces it without blocking. A reviewer may hold '
   'that it should not be raised at all.',
   'Drafted as a warning, on the basis that the duty is the new owner.'),

  ('OK-TITLE-VIN-INSPECTION-001', 7,
   'Do subsection L exclusions attach where the rule assumes? It opens "Except '
   'for all-terrain vehicles, utility vehicles and motorcycles used exclusively for '
   'off-road use" and then excepts interstate-commerce commercial trucks, '
   'truck-tractors, trailers and semitrailers. The rule reads all of those as '
   'outside the requirement.',
   'Drafted reading every listed exception as outside the requirement. The '
   'sentence is long enough that the attachment should be confirmed.')
) as q(rule_code, ord, question, reading)
join compliance.regulatory_rule r
  on r.rule_code = q.rule_code and r.jurisdiction_code = 'US-OK'
join compliance.regulatory_rule_version v
  on v.rule_id = r.id and v.approved_at is null
 and v.version = (
   select max(x.version) from compliance.regulatory_rule_version x
    where x.rule_id = r.id and x.approved_at is null)
where not exists (
  select 1 from compliance.pending_approval p
   where p.item_kind = 'interpretation' and p.question = q.question);

-- 3. The two questions the statute does not answer, which is why no reading of
--    it was drafted at all.
insert into compliance.pending_approval
  (item_kind, jurisdiction_code, question, drafter_reading, reviewer_discipline,
   reference_document, sort_order)
select 'statutory_question', 'US-OK', q.question, q.reading, 'attorney',
       'db/migrations/V0013__oklahoma_hb3982_hb3148_draft.sql', 300 + q.ord
from (values
  (1,
   'What does a DEALER sale between 1 November and 31 December 2026 require? '
   'HB 3982 definition calls the ten-day plate the plate "for private sales", '
   'while the sixty-day dealer plate "begins issuing" on 1 January 2027. The two '
   'months in between are not covered by either reading, and no rule was drafted '
   'for them.',
   'Not drafted. The enrolled text does not resolve it and a guess would govern '
   'every Oklahoma dealer sale for two months.'),

  (2,
   'Does OK-PLATE-TRANSFER-001 have a successor at all? From 1 January 2027 '
   'subdivision (a) says that on a dealer sale "no license plate shall be issued '
   'and instead the dealer seller shall issue a sixty-day temporary license plate", '
   'while subdivision (3) still speaks of issuing "a temporary decal to the '
   'existing license plate" where a plate was transferred.',
   'Not drafted. The two subdivisions appear to describe different worlds and the '
   'reconciliation is a legal judgement.')
) as q(ord, question, reading)
where not exists (
  select 1 from compliance.pending_approval p
   where p.item_kind = 'statutory_question' and p.question = q.question);

-- 4. The tax rule set, which needs a different profession entirely.
insert into compliance.pending_approval
  (item_kind, tax_rule_set_id, jurisdiction_code, question, drafter_reading,
   reviewer_discipline, reference_document, sort_order)
select
  'tax_rule_set',
  s.id,
  'US-OK',
  'Is the Oklahoma excise tax drafted in rule set version ' || s.version ||
    ' correct, and is it complete enough to price a deal? It carries the banded '
    'rate and dollar rounding from 68 O.S. 2103 and the basis from 2104, and '
    'nothing else. Nine fields remain UNVERIFIED and are registered separately.',
  'Drafted from archived primary text only: $20 on the first $1,500 of value plus '
  '3.25% of the remainder, rounded to the nearest dollar, on a basis of actual '
  'sales price. NOT a tax opinion. Covers USED vehicles; the new-vehicle rate in '
  'the same section is a flat 3.25% and is not expressible in the same rule set '
  'row, which is itself a question for the reviewer.',
  'cpa_or_tax_counsel',
  'docs/16-oklahoma-tax-research.md',
  400
from sales.rule_set s
join sales.tax_jurisdiction j on j.id = s.jurisdiction_id
where j.state_code = 'OK' and s.schema_version = 2 and s.approved_at is null
  and not exists (
    select 1 from compliance.pending_approval p where p.tax_rule_set_id = s.id);

-- 5. The nine tax fields no primary source has answered. Registered one per row
--    so that each can be closed as its source is found, rather than the whole
--    rule set staying open until the last one lands.
insert into compliance.pending_approval
  (item_kind, jurisdiction_code, question, drafter_reading, reviewer_discipline,
   reference_document, sort_order)
select 'interpretation', 'US-OK',
       'Oklahoma tax field "' || f.field || '": ' || f.question,
       'Left UNVERIFIED in the drafted rule set. 68 O.S. 2103 and 2104 do not '
       'answer it, and no secondary source may back a production rule.',
       'cpa_or_tax_counsel', 'docs/16-oklahoma-tax-research.md', 500 + f.ord
from (values
  (1, 'tradeInCreditPolicy',
   'does a trade-in reduce the taxable amount, and is there a cap? The off-road '
   'rate is expressly levied "before any discounts or credits are given for a '
   'trade-in", but an exclusion in one rate is not a rule about the others.'),
  (2, 'vehicleSalesTax',
   'is there a vehicle sales tax alongside the excise tax, and at what rate?'),
  (3, 'localRateSource',
   'does municipal or county sales tax reach a vehicle sale at all, and if so is '
   'it sourced to the destination or the origin?'),
  (4, 'rebateTaxable',
   'does a manufacturer rebate reduce the "actual sales price"?'),
  (5, 'docFeeCap',
   'is there a statutory or commission limit on a dealer documentation fee, and '
   'is the fee taxable?'),
  (6, 'titleFee',
   'what is the title fee, and under which Title 47 provision?'),
  (7, 'registrationFees',
   'what are the registration and tag fees, which vary by vehicle age and type?'),
  (8, 'maximumTax',
   'does Oklahoma cap vehicle tax at all? Probably not, but "probably not" is not '
   'a citation.'),
  (9, 'accessoriesTaxable',
   'do dealer-added accessories form part of the actual sales price?')
) as f(ord, field, question)
where not exists (
  select 1 from compliance.pending_approval p
   where p.item_kind = 'interpretation'
     and p.question like 'Oklahoma tax field "' || f.field || '"%');

-- 6. The archived sources are a living risk, not a one-off task.
insert into compliance.pending_approval
  (item_kind, jurisdiction_code, question, drafter_reading, reviewer_discipline,
   reference_document, blocks_go_live, sort_order)
select 'source_currency', 'US-OK',
  'Are the archived Oklahoma sources still current? Each carries a URL and a '
  'recorded sha256, and CI re-hashes the files on every build - but that proves '
  'only that OUR copy has not changed, never that the published text has not. '
  'The Service Oklahoma Electronic Titles page is a living page.',
  'Three documents were transcribed and supplied by the project owner; two '
  'enrolled bills and two statutes were retrieved by repository tooling. '
  'db/sources/us-ok/README.md distinguishes the custodies.',
  'attorney', 'db/sources/us-ok/README.md', true, 600
where not exists (
  select 1 from compliance.pending_approval p where p.item_kind = 'source_currency');

--------------------------------------------------------------------------------
-- Assert the register is populated, and say what is in it.
--------------------------------------------------------------------------------
do $$
declare
  versions int;
  interpretations int;
  questions int;
  tax_sets int;
  total int;
  resolved int;
begin
  select count(*) into versions
    from compliance.pending_approval where item_kind = 'rule_version';
  select count(*) into interpretations
    from compliance.pending_approval where item_kind = 'interpretation';
  select count(*) into questions
    from compliance.pending_approval where item_kind = 'statutory_question';
  select count(*) into tax_sets
    from compliance.pending_approval where item_kind = 'tax_rule_set';
  select count(*) into total from compliance.pending_approval;
  select count(*) into resolved
    from compliance.pending_approval where resolved_at is not null;

  if versions <> 10 then
    raise exception
      'Expected 10 drafted Oklahoma rule versions in the register, found %. '
      'V0012 drafts six and V0013 drafts four; if that is no longer true this '
      'migration is registering the wrong thing.', versions;
  end if;

  if questions <> 2 then
    raise exception
      'Expected the 2 statutory questions V0013 leaves open, found %.', questions;
  end if;

  if tax_sets <> 1 then
    raise exception
      'Expected the 1 drafted Oklahoma tax rule set from V0016, found %. Check '
      'that V0016 applied and that its row is still unapproved.', tax_sets;
  end if;

  if resolved <> 0 then
    raise exception
      'Something is already resolved in a freshly seeded register (% rows). '
      'Nothing here may be disposed of by a migration.', resolved;
  end if;

  raise notice
    'Approval register seeded: % items awaiting a human, none resolved. % rule '
    'versions and % interpretations for an Oklahoma attorney, % statutory '
    'questions nobody should answer from the text alone, and % tax rule set for '
    'a CPA or tax counsel. Nothing here activates a rule - only an approved '
    'version row does that.',
    total, versions, interpretations, questions, tax_sets;
end
$$;
