--------------------------------------------------------------------------------
-- MautoDesk — V0015 Oklahoma tax statutes, archived
--
-- ============================================================================
-- THIS MIGRATION ADDS NO TAX NUMBER TO ANY RULE SET. IT ARCHIVES TWO STATUTES.
-- ============================================================================
--
-- WHY ONLY THIS MUCH
--
-- sales.rule_set for OK/KS/TX has been a skeleton since V0007: taxBasis
-- 'UNVERIFIED', null rates, no fees, and the citation 'PLACEHOLDER — must cite
-- the governing statute or DMV publication before approval'. The deal engine
-- loads only approved rows, so it refuses to price any deal, which is the
-- correct behaviour and not a bug.
--
-- Filling that skeleton needs a source per field. Every readily available
-- summary of Oklahoma vehicle tax — calculators, legal-publisher reproductions,
-- dealer guides — is a secondary source, and this project does not let a
-- secondary source back a production rule. So the first move is to archive the
-- primary text, and the second is to know exactly which fields it does and does
-- not answer. docs/16-oklahoma-tax-research.md is that field-by-field matrix.
--
-- Two sections are archived here, from the Oklahoma State Courts Network, which
-- publishes the statutes as currently in force:
--
--   68 O.S. § 2103  Tax for Transfer of Vehicle Ownership — the excise tax
--   68 O.S. § 2104  Value of Vehicles — what the tax is levied on
--
-- Both carry 2025 amendments, which is the point of archiving rather than
-- remembering: § 2103 was amended by Laws 2025, HB 1751, c. 330, § 23, emerg.
-- eff. July 1, 2025, and § 2104 by Laws 2025, HB 1183, c. 252, § 1, eff. July 1,
-- 2026. Anything written from memory about Oklahoma vehicle tax predates both.
--
-- WHAT THESE TWO SECTIONS ESTABLISH
--
-- Quoted so a reviewer checks the statute rather than this comment:
--
--   "The excise tax for new vehicles shall be levied at three and one-fourth
--    percent (3 1/4%) of the value of each new vehicle."
--
--   Used vehicles, "for the year beginning July 1, 2002, and all subsequent
--    years, Twenty Dollars ($20.00) on the first One Thousand Five Hundred
--    Dollars ($1,500.00) or less of value of such vehicle, and three and
--    one-fourth percent (3 1/4%) of the remaining value of such vehicle."
--
--   "The value of any vehicle, for purposes of the excise tax levied by Section
--    2103 of this title, shall be the actual sales price of such a vehicle."
--
-- WHAT THEY DO NOT ESTABLISH, AND NOBODY SHOULD INFER
--
-- Whether a trade-in reduces that "actual sales price" is not stated for
-- ordinary vehicles. The off-road rate in § 2103 is expressly levied "before any
-- discounts or credits are given for a trade-in", and an exclusion written into
-- one rate is not a rule about the others. The deal engine's
-- tradeInCreditPolicy therefore stays UNVERIFIED: a reading either way would be
-- a guess about money on a signed contract.
--
-- Also absent from both sections, and each needing its own source: the vehicle
-- sales tax that sits alongside the excise tax, title and registration fees,
-- any limit on a dealer documentation fee, and local rate sourcing. They are in
-- Title 47 and in agency rules, not here.
--------------------------------------------------------------------------------

insert into compliance.regulatory_source
  (id, authority_id, source_type, title, citation, url) values
  ('05000000-0000-4000-8000-000000000007',
   '0c000000-0000-4000-8000-000000000005', 'statute',
   'Tax for Transfer of Vehicle Ownership',
   '68 O.S. § 2103 (OSCN 2026), as amended by Laws 2025, HB 1751, c. 330, § 23, emerg. eff. July 1, 2025',
   'https://www.oscn.net/applications/oscn/DeliverDocument.asp?CiteID=440208'),

  ('05000000-0000-4000-8000-000000000008',
   '0c000000-0000-4000-8000-000000000005', 'statute',
   'Value of Vehicles',
   '68 O.S. § 2104 (OSCN 2026), as amended by Laws 2025, HB 1183, c. 252, § 1, eff. July 1, 2026',
   'https://www.oscn.net/applications/oscn/DeliverDocument.asp?CiteID=438854')
on conflict (authority_id, citation) do nothing;

insert into compliance.regulatory_source_version
  (id, source_id, version, retrieved_at, retrieved_by, content_hash, content_location,
   effective_from, change_summary, last_verified_at)
values
  ('05100000-0000-4000-8000-000000000007',
   '05000000-0000-4000-8000-000000000007', 1,
   timestamptz '2026-11-14 00:00:00+00',
   'Retrieved from oscn.net by repository tooling — NOT a tax opinion',
   decode('05646e594c37d5c3fc36031244b07c0325cf0a42e92bb10c99cf9e35720eb28e', 'hex'),
   'db/sources/us-ok/ok-68-os-2103-excise-tax.txt',
   date '2025-07-01',
   'Primary text of the vehicle excise tax as OSCN publishes it in force, with '
   'its Historical Data block so the amendment chain is visible. OSCN''s own '
   'navigation and its Citationizer tables are omitted; the statute and its '
   'history are complete.',
   timestamptz '2026-11-14 00:00:00+00'),

  ('05100000-0000-4000-8000-000000000008',
   '05000000-0000-4000-8000-000000000008', 1,
   timestamptz '2026-11-14 00:00:00+00',
   'Retrieved from oscn.net by repository tooling — NOT a tax opinion',
   decode('e48c934ba5626955561b7fd2406421e90f8db6d3b34524ae440334b8ed2ba081', 'hex'),
   'db/sources/us-ok/ok-68-os-2104-vehicle-value.txt',
   date '2026-07-01',
   'Primary text of what the excise tax is levied on. Establishes the basis as '
   '"the actual sales price", and is silent on trade-in treatment for ordinary '
   'vehicles — which is why tradeInCreditPolicy stays UNVERIFIED rather than '
   'being inferred from the off-road rate''s express exclusion.',
   timestamptz '2026-11-14 00:00:00+00')
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- The rule set is deliberately untouched. Say so in the log, because a migration
-- named "tax sources" that changes no tax could otherwise read as a mistake.
--------------------------------------------------------------------------------
do $$
declare
  unverified int;
begin
  select count(*) into unverified
    from sales.rule_set
   where approved_at is null
     and (rules->>'taxBasis') = 'UNVERIFIED';

  raise notice
    'Oklahoma tax sources archived: 2 statutes, hashed and cited. % rule set(s) '
    'remain UNVERIFIED and unapproved, so the deal engine still refuses to price '
    'a deal. Filling them needs a source per field — see '
    'docs/16-oklahoma-tax-research.md — and a CPA or tax counsel, not a '
    'migration.', unverified;
end
$$;
