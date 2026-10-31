--------------------------------------------------------------------------------
-- MautoDesk — V0012 Oklahoma regulatory rule pack (DRAFT)
--
-- ============================================================================
-- NOTHING IN THIS MIGRATION IS APPROVED, AND NOTHING IN IT IS EVALUATED.
-- ============================================================================
--
-- Every rule version below is seeded with status 'draft' and approved_at NULL,
-- which makes it invisible to the regulatory engine: RuleEvaluator filters on
-- GovernsOn(), which requires approval, and RegulatoryRuleStore's query filters
-- on approved_at again. An Oklahoma deal evaluated today returns GREEN, and
-- GREEN means "no rule has been approved" — NOT "this deal is lawful".
--
-- WHAT THE RULES WERE WRITTEN AGAINST
--
-- The three documents in db/sources/us-ok/ are the PRIMARY TEXT as published by
-- Service Oklahoma — the press release, the 11-page dealer FAQ, and the
-- electronic titles page and FAQ — archived verbatim and hashed. The
-- source_version rows below carry the real SHA-256 of each archived file, and
-- RegulatoryRuleTests re-hashes them on every build, so "this rule was written
-- against this text" is checkable rather than asserted.
--
-- Every rule below was read against that text and corrected where it differed.
-- The specifics — two business days, ten calendar days, the thirteen exempt
-- registration types, the paper-title exceptions, the notarisation requirement,
-- the VIN-inspection hold — are quoted in each rule's approval note so a
-- reviewer can check the rule against the sentence it came from.
--
-- WHAT A HUMAN MUST STILL DO
--
-- The source bar is met. The REVIEW bar is not, and that is the only thing
-- standing between these drafts and an active pack:
--
--   1. Have dealer-compliance counsel read each rule against the archived text
--      and confirm the reading — particularly the three interpretations flagged
--      in the approval notes, where the source is silent or broader than the
--      rule.
--   2. Populate compliance.jurisdiction_holiday for US-OK from a cited source.
--      OK-PREREG-001 carries a business-day deadline, and until the calendar
--      exists the engine refuses to compute one rather than guessing.
--   3. Set status, approved_at and approved_by to the reviewer's name. A
--      developer must not do this, and CI fails if a migration tries.
--
-- Chain of custody: the archived files were transcribed from the Service
-- Oklahoma page and PDF and supplied by the project owner, not fetched from
-- oklahoma.gov by this migration. They read as verbatim — press-release voice,
-- PDF line-wrapping, direct quotations — and agree with what an independent web
-- search returned. A reviewer should still open the URLs in
-- regulatory_source.url and confirm the text has not changed since.
--------------------------------------------------------------------------------

insert into compliance.regulatory_jurisdiction (code, level, name) values
  ('US',    'federal', 'United States'),
  ('US-OK', 'state',   'Oklahoma')
on conflict (code) do nothing;

update compliance.regulatory_jurisdiction set parent_code = 'US' where code = 'US-OK';

--------------------------------------------------------------------------------
-- Authorities.
--------------------------------------------------------------------------------
insert into compliance.regulatory_authority (id, jurisdiction_code, code, name, website_url) values
  ('0c000000-0000-4000-8000-000000000001', 'US-OK', 'SERVICE-OK', 'Service Oklahoma',
   'https://oklahoma.gov/service.html'),
  ('0c000000-0000-4000-8000-000000000002', 'US-OK', 'OTC', 'Oklahoma Tax Commission',
   'https://oklahoma.gov/tax.html'),
  ('0c000000-0000-4000-8000-000000000003', 'US-OK', 'OUMVPC',
   'Oklahoma Used Motor Vehicle, Dismantler and Parts Commission',
   'https://oklahoma.gov/oumvpc.html'),
  ('0c000000-0000-4000-8000-000000000004', 'US-OK', 'OMVC',
   'Oklahoma Motor Vehicle Commission', 'https://oklahoma.gov/omvc.html')
on conflict (jurisdiction_code, code) do nothing;

--------------------------------------------------------------------------------
-- Sources. These describe the ORIGINAL documents.
--------------------------------------------------------------------------------
insert into compliance.regulatory_source
  (id, authority_id, source_type, title, citation, url) values
  ('05000000-0000-4000-8000-000000000001',
   '0c000000-0000-4000-8000-000000000001',
   'published_guidance',
   'New Vehicle Pre-Registration Process Is Now in Effect',
   'Service Oklahoma press release, 3 September 2024 (SB2035, the Mason Treat Act)',
   'https://oklahoma.gov/service/newsroom/sok-new-vehicle-pre-registration-process.html'),

  ('05000000-0000-4000-8000-000000000002',
   '0c000000-0000-4000-8000-000000000001',
   'form_instruction',
   'SB2035 FAQ for Motor Vehicle Dealers',
   'Service Oklahoma SB2035 dealer FAQ, updated 23 August 2024',
   'https://oklahoma.gov/content/dam/ok/en/onmvc/documents/24-035%20MV%20Groups-SB2035-FAQ%2020.pdf'),

  ('05000000-0000-4000-8000-000000000003',
   '0c000000-0000-4000-8000-000000000001',
   'published_guidance',
   'Electronic Titles',
   'Service Oklahoma electronic title guidance, effective 1 July 2025',
   'https://oklahoma.gov/service/all-services/auto-vehicle/electronic-titles.html')
on conflict (authority_id, citation) do nothing;

--------------------------------------------------------------------------------
-- Source versions. These describe WHAT WAS ACTUALLY RETRIEVED AND READ.
--
-- content_hash is the real SHA-256 of the archived file named in
-- content_location, and the archived file is the primary text.
--
-- Written as decode(..., 'hex') rather than a '\x...' bytea literal. The escape
-- form loses its leading byte to any tooling that interprets backslashes on
-- the way in, and the result is a hash that is subtly wrong rather than
-- obviously missing — which is exactly the failure the hash test caught when
-- this migration was first written that way. Check it:
--
--   sha256sum db/sources/us-ok/service-ok-sb2035-dealer-faq.md
--
-- An earlier revision of this migration cited AI-authored consolidations of
-- these documents and said so. Those are gone: the primary text is available,
-- so citing a paraphrase of it would be a choice, not a limitation.
--------------------------------------------------------------------------------
insert into compliance.regulatory_source_version
  (id, source_id, version, retrieved_at, retrieved_by, content_hash, content_location,
   effective_from, change_summary, last_verified_at)
values
  ('05100000-0000-4000-8000-000000000001',
   '05000000-0000-4000-8000-000000000001', 1,
   timestamptz '2026-08-22 00:00:00+00',
   'Verbatim transcription supplied by the project owner — NOT a legal review',
   decode('61b3bf2f20312c23e3cc5b6f02e9c7d09498b647d60d6a20b141af2b83933816', 'hex'),
   'db/sources/us-ok/service-ok-pre-registration-release.md',
   date '2024-09-01',
   'Primary text: the Service Oklahoma press release of 3 September 2024, '
   'archived verbatim. Confirm against the URL that the page has not changed '
   'since before approving.',
   null),

  ('05100000-0000-4000-8000-000000000002',
   '05000000-0000-4000-8000-000000000002', 1,
   timestamptz '2026-08-22 00:00:00+00',
   'Verbatim transcription supplied by the project owner — NOT a legal review',
   decode('162acb1b68b164f6358a376d26e42777e552d5e2d4dca94540dde27cc51784e9', 'hex'),
   'db/sources/us-ok/service-ok-sb2035-dealer-faq.md',
   date '2024-09-01',
   'Primary text: the 11-page SB2035 FAQ for Motor Vehicle Dealers, updated '
   '23 August 2024, archived verbatim including its page furniture and the '
   '23 August correction notice listing the pre-registration data fields.',
   null),

  ('05100000-0000-4000-8000-000000000003',
   '05000000-0000-4000-8000-000000000003', 1,
   timestamptz '2026-08-22 00:00:00+00',
   'Verbatim transcription supplied by the project owner — NOT a legal review',
   decode('9406f4b875d95d0534667e96c6e604decc197786f1c0f405764a7e6492ef4ac6', 'hex'),
   'db/sources/us-ok/service-ok-electronic-titles.md',
   date '2025-07-01',
   'Primary text: the Service Oklahoma Electronic Titles page and its full FAQ, '
   'archived verbatim. This is a living page; confirm it has not changed before '
   'approving, and append a new version rather than editing this row if it has.',
   null)
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- Rules.
--------------------------------------------------------------------------------
insert into compliance.regulatory_rule (id, rule_code, jurisdiction_code, category, title, description)
values
  ('0a000000-0000-4000-8000-000000000001', 'OK-PREREG-001', 'US-OK', 'registration',
   'Pre-registration must be submitted within two business days of sale',
   'DRAFT. The dealer FAQ states a dealer must submit pre-registration '
   'information within two business days of a sale, business days being Monday '
   'to Friday excluding state holidays, and that the requirement applies to all '
   'new and used vehicles purchased in Oklahoma on or after 1 September 2024 — '
   'including vehicles that will ultimately be registered in another state, '
   'another jurisdiction, or with a tribal nation. Thirteen registration types '
   'are exempt. The deadline is a rule field, never a constant.'),

  ('0a000000-0000-4000-8000-000000000002', 'OK-TEMP-TAG-001', 'US-OK', 'temporary_tag',
   'A buyer not transferring an existing plate needs a ten-day temporary paper tag',
   'DRAFT. Oklahoma plates are assigned to people rather than vehicles, so a '
   'buyer often transfers an existing plate. Where they do not, the dealer '
   'issues a temporary paper tag; the FAQ states the tag is valid ten days and '
   'that the date printed on it is the expiry, ten days from the date of sale. '
   'Ten CALENDAR days, not business days — the source says "ten days from the '
   'date of sale" while stating the pre-registration deadline separately in '
   'business days, so the distinction is deliberate.'),

  ('0a000000-0000-4000-8000-000000000003', 'OK-PLATE-TRANSFER-001', 'US-OK', 'registration',
   'A transferred plate must be affixed and its details submitted',
   'DRAFT. Where the buyer transfers an existing Oklahoma metal plate, the '
   'dealer affixes that plate to the new vehicle and enters the plate details '
   'during pre-registration instead of issuing a temporary tag. Members of '
   'tribal nations other than the Chickasaw and Choctaw Nations cannot transfer '
   'their existing tribal plates and therefore do need a temporary tag, which '
   'this rule does not yet model.'),

  ('0a000000-0000-4000-8000-000000000004', 'OK-TITLE-TYPE-001', 'US-OK', 'title',
   'The title type must be established before contracting',
   'DRAFT. From 1 July 2025 Oklahoma issues titles electronically, but licensed '
   'dealers continue to receive PAPER titles by default on initial title '
   'transactions and may opt in to electronic. Paper may also be requested for '
   'auction sales, vehicles registered or sold outside Oklahoma, floorplan '
   'lending, and leases. This rule therefore refuses an UNKNOWN title type '
   'rather than assuming either answer.'),

  ('0a000000-0000-4000-8000-000000000005', 'OK-TITLE-ETBS-001', 'US-OK', 'title',
   'Transferring an electronically titled vehicle needs a notarised Electronic Title Bill of Sale',
   'DRAFT. Where a vehicle already carries an electronic title, the Electronic '
   'Title Bill of Sale replaces the physical title for the transfer, and the '
   'source states it must be notarised. A vehicle with a paper title or an MCO '
   'is out of scope.'),

  ('0a000000-0000-4000-8000-000000000006', 'OK-TITLE-VIN-INSPECTION-001', 'US-OK', 'title',
   'An out-of-state title requires a completed VIN inspection',
   'DRAFT. VIN inspections for out-of-state titles remain the responsibility of '
   'licensed operators. A dealer slip-in title request submitted before the '
   'inspection is completed is PLACED ON HOLD until it is — which is the '
   'concrete, sourced form of the title-hold concept, replacing the unsourced '
   'placeholder rule this pack previously carried.')
on conflict (rule_code) do nothing;

--------------------------------------------------------------------------------
-- Rule versions. All draft, all unapproved, none evaluated.
--------------------------------------------------------------------------------
insert into compliance.regulatory_rule_version
  (id, rule_id, version, status, effective_from, source_version_id, trigger_event,
   definition, severity, approval_note)
values
  -- OK-PREREG-001 -----------------------------------------------------------
  -- Effective from the date the process began. A vehicle purchased before then
  -- is not subject to it, and rule effective-dating is what expresses that.
  ('0b000000-0000-4000-8000-000000000001',
   '0a000000-0000-4000-8000-000000000001', '0.2.0-draft', 'draft', date '2024-09-01',
   '05100000-0000-4000-8000-000000000002', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "deal.type", "operator": "in", "values": ["retail", "consignment"] },
         { "field": "vehicle.registrationType", "operator": "not_in",
           "values": ["atv_utv", "boat", "commercial_trailer", "commercial_rental_trailer",
                      "construction_machinery", "farm_trailer", "forest_trailer",
                      "manufactured_home", "mobile_chapel", "off_road_motorcycle",
                      "private_trailer", "special_mobilized_trailer", "trailer_exempt"] }
       ]
     },
     "actions": [
       { "require": { "code": "PRE_REGISTRATION",
                      "name": "Vehicle pre-registration submission to Service Oklahoma" } },
       { "deadline": { "code": "PRE_REGISTRATION", "days": 2, "businessDays": true } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source: "dealers will have two business days to submit '
   'pre-registration information online. Business days are defined as Mondays '
   'through Fridays excluding state holidays." The thirteen exempt registration '
   'types are the list under "What vehicles are exempt from pre-registration?", '
   'transcribed exactly. '
   'INTERPRETATION TO CONFIRM: the source says the process "applies to all '
   'vehicles (new or used) purchased in the state of Oklahoma", with no '
   'wholesale carve-out; this rule restricts itself to retail and consignment '
   'deals. That narrowing is a reading, not something the text states. It also '
   'applies "regardless of whether the vehicle will be fully registered in '
   'another state, jurisdiction or with a tribe", which this rule correctly does '
   'not exclude.'),

  -- OK-TEMP-TAG-001 ---------------------------------------------------------
  ('0b000000-0000-4000-8000-000000000002',
   '0a000000-0000-4000-8000-000000000002', '0.2.0-draft', 'draft', date '2024-09-01',
   '05100000-0000-4000-8000-000000000002', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "deal.type", "operator": "in", "values": ["retail", "consignment"] },
         { "field": "deal.transfersExistingPlate", "operator": "equals", "value": "false" }
       ]
     },
     "actions": [
       { "require": { "code": "TEMPORARY_PAPER_TAG",
                      "name": "Ten-day temporary paper tag from an approved vendor" } },
       { "deadline": { "code": "TEMPORARY_PAPER_TAG", "days": 10, "businessDays": false } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source: "The date on the paper tag should be the date that the paper '
   'tag expires: ten days from the date of sale." Ten CALENDAR days — the same '
   'document states the pre-registration deadline separately in business days, '
   'so the two units are deliberately different and neither is an assumption. '
   'NOT CHECKED BY THIS RULE: "dealers must order temporary paper tags through '
   'their motor vehicle commission-approved vendors", which needs a vendor list '
   'this system does not yet hold.'),

  -- OK-PLATE-TRANSFER-001 ---------------------------------------------------
  ('0b000000-0000-4000-8000-000000000003',
   '0a000000-0000-4000-8000-000000000003', '0.1.0-draft', 'draft', date '2024-09-01',
   '05100000-0000-4000-8000-000000000002', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "deal.transfersExistingPlate", "operator": "equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "TRANSFERRED_PLATE_DETAILS",
                      "name": "Existing plate affixed and its details submitted with pre-registration" } }
     ]
   }
   $json$::jsonb,
   'warning',
   'DRAFT. Source: "In Oklahoma, license plates are assigned to people, not '
   'vehicles… dealers should ensure these plates are affixed to the newly '
   'purchased vehicles." '
   'INTERPRETATION TO CONFIRM: warning rather than blocking, because the text '
   'says the dealer "should" rather than stating a prohibition. Whether that '
   'reading is right is a reviewer''s decision, not a developer''s. Also not '
   'modelled: members of tribal nations other than the Chickasaw and Choctaw '
   'Nations cannot transfer an existing tribal plate and so do need a temporary '
   'tag.'),

  -- OK-TITLE-TYPE-001 -------------------------------------------------------
  ('0b000000-0000-4000-8000-000000000004',
   '0a000000-0000-4000-8000-000000000004', '0.2.0-draft', 'draft', date '2025-07-01',
   '05100000-0000-4000-8000-000000000003', 'DEAL_CONTRACTED',
   $json$
   {
     "conditions": {
       "any": [
         { "field": "title.type", "operator": "not_exists" },
         { "field": "title.type", "operator": "equals", "value": "unknown" }
       ]
     },
     "actions": [
       { "prohibit": { "message": "The title type has not been established. Oklahoma issues titles electronically from 1 July 2025, but a licensed dealer receives a paper title by default on an initial title transaction, and paper may also apply to an auction sale, an out-of-state sale, floorplan lending or a lease. Determine which applies rather than assuming." } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source: "Dealers will continue to receive paper titles by default '
   'during initial title transactions… Dealers will also be able to opt in to '
   'receive electronic titles if they wish." Paper may also be requested where '
   '"The vehicle is sold at auction", "The vehicle is registered or sold out of '
   'state", "The vehicle is used for floorplan lending", or the vehicle is '
   'leased. This rule therefore deliberately does NOT encode "all Oklahoma '
   'titles are electronic", which the text contradicts for exactly the dealer '
   'case this product serves.'),

  -- OK-TITLE-ETBS-001 -------------------------------------------------------
  ('0b000000-0000-4000-8000-000000000005',
   '0a000000-0000-4000-8000-000000000005', '0.1.0-draft', 'draft', date '2025-07-01',
   '05100000-0000-4000-8000-000000000003', 'DEAL_CONTRACTED',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "title.type", "operator": "equals", "value": "electronic" },
         { "field": "deal.isNewVehicle", "operator": "equals", "value": "false" }
       ]
     },
     "actions": [
       { "require": { "code": "ELECTRONIC_TITLE_BILL_OF_SALE",
                      "name": "Notarised Electronic Title Bill of Sale",
                      "documentTemplateCode": "OK_ETBS" } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source, answering "Is the notary requirement eliminated due to the '
   'changes?": "No. The Electronic Title Bill of Sale that''s used to transfer '
   'ownership of a vehicle with an electronic title needs to be notarized." '
   'Unambiguous, which is why this one is blocking. '
   'NOT CHECKED BY THIS RULE: whether the document actually bears a notarisation '
   '— the document engine must verify that, and it is not built.'),

  -- OK-TITLE-VIN-INSPECTION-001 ---------------------------------------------
  ('0b000000-0000-4000-8000-000000000006',
   '0a000000-0000-4000-8000-000000000006', '0.2.0-draft', 'draft', date '2025-07-01',
   '05100000-0000-4000-8000-000000000003', 'TITLE_SLIP_IN_REQUESTED',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "title.isOutOfState", "operator": "equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "VIN_INSPECTION",
                      "name": "VIN inspection at a licensed operator" } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source: "VIN inspections are only required for out-of-state vehicle '
   'titles" and "If a VIN inspection is not completed when a slip-in title '
   'request is submitted, the request would be put on hold until the VIN '
   'inspection is completed." '
   'CORRECTED against the primary text: an earlier draft fired this on '
   'DEAL_CONTRACTED. The text puts the hold on the SLIP-IN TITLE REQUEST, not on '
   'contracting, so the trigger is TITLE_SLIP_IN_REQUESTED. Whether an '
   'outstanding inspection should ALSO stop a deal being contracted is a '
   'business decision the source does not make.')
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- Rule tests.
--
-- Every rule ships with a case that makes it fire and a case that does not. A
-- rule with only a firing case has been shown to be a constant, not a rule.
-- RegulatoryRuleTests executes these against the evaluator in CI, so the moment
-- a reviewer approves a rule its tests are already green — or already red.
--------------------------------------------------------------------------------
-- Two cases with the same name on one rule version is a mistake, and without a
-- constraint saying so a re-applied migration silently doubles the corpus.
create unique index if not exists regulatory_rule_test_name_uq
  on compliance.regulatory_rule_test (rule_version_id, name);

insert into compliance.regulatory_rule_test (rule_version_id, name, expectation, input)
values
  -- OK-PREREG-001
  ('0b000000-0000-4000-8000-000000000001',
   'A retail sale of an ordinary automobile requires pre-registration', 'block',
   '{"deal.type":"retail","vehicle.registrationType":"automobile"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000001',
   'A retail sale with the submission on file passes', 'pass',
   '{"deal.type":"retail","vehicle.registrationType":"automobile","_satisfied":["PRE_REGISTRATION"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000001',
   'A wholesale deal is out of scope', 'pass',
   '{"deal.type":"wholesale","vehicle.registrationType":"automobile"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000001',
   'A boat is an exempt registration type', 'pass',
   '{"deal.type":"retail","vehicle.registrationType":"boat"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000001',
   'A private trailer is an exempt registration type', 'pass',
   '{"deal.type":"retail","vehicle.registrationType":"private_trailer"}'::jsonb),

  -- OK-TEMP-TAG-001
  ('0b000000-0000-4000-8000-000000000002',
   'A retail buyer taking a new plate needs a temporary tag', 'block',
   '{"deal.type":"retail","deal.transfersExistingPlate":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000002',
   'A buyer transferring an existing plate does not', 'pass',
   '{"deal.type":"retail","deal.transfersExistingPlate":"true"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000002',
   'A tag already issued satisfies the requirement', 'pass',
   '{"deal.type":"retail","deal.transfersExistingPlate":"false","_satisfied":["TEMPORARY_PAPER_TAG"]}'::jsonb),

  -- OK-PLATE-TRANSFER-001
  ('0b000000-0000-4000-8000-000000000003',
   'Transferring a plate warns until the details are submitted', 'warning',
   '{"deal.transfersExistingPlate":"true"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000003',
   'Details submitted clears the warning', 'pass',
   '{"deal.transfersExistingPlate":"true","_satisfied":["TRANSFERRED_PLATE_DETAILS"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000003',
   'A buyer taking a new plate is out of scope', 'pass',
   '{"deal.transfersExistingPlate":"false"}'::jsonb),

  -- OK-TITLE-TYPE-001
  ('0b000000-0000-4000-8000-000000000004',
   'An unknown title type blocks contracting', 'block',
   '{"title.type":"unknown"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000004',
   'An absent title type blocks contracting', 'block',
   '{"deal.type":"retail"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000004',
   'A paper title is established and passes', 'pass',
   '{"title.type":"paper"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000004',
   'An electronic title is established and passes', 'pass',
   '{"title.type":"electronic"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000004',
   'An MCO on a new vehicle is established and passes', 'pass',
   '{"title.type":"mco"}'::jsonb),

  -- OK-TITLE-ETBS-001
  ('0b000000-0000-4000-8000-000000000005',
   'A used vehicle with an electronic title needs the bill of sale', 'block',
   '{"title.type":"electronic","deal.isNewVehicle":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000005',
   'The bill of sale on file passes', 'pass',
   '{"title.type":"electronic","deal.isNewVehicle":"false","_satisfied":["ELECTRONIC_TITLE_BILL_OF_SALE"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000005',
   'A paper title is out of scope', 'pass',
   '{"title.type":"paper","deal.isNewVehicle":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000005',
   'A new vehicle carrying an MCO is out of scope', 'pass',
   '{"title.type":"electronic","deal.isNewVehicle":"true"}'::jsonb),

  -- OK-TITLE-VIN-INSPECTION-001
  ('0b000000-0000-4000-8000-000000000006',
   'An out-of-state title requires a VIN inspection', 'block',
   '{"title.isOutOfState":"true"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000006',
   'A completed inspection passes', 'pass',
   '{"title.isOutOfState":"true","_satisfied":["VIN_INSPECTION"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000006',
   'An Oklahoma title is out of scope', 'pass',
   '{"title.isOutOfState":"false"}'::jsonb)
on conflict (rule_version_id, name) do nothing;

--------------------------------------------------------------------------------
-- Verification: nothing here is approved, and nothing here is active.
--
-- Asserted at migration time so that approving a draft by accident — a stray
-- UPDATE in a later migration, a copy-paste — fails the deployment rather than
-- quietly starting to block deals against unreviewed rules.
--------------------------------------------------------------------------------
do $$
declare
  approved int;
  rules    int;
  tests    int;
begin
  select count(*) into approved
    from compliance.regulatory_rule_version v
    join compliance.regulatory_rule r on r.id = v.rule_id
   where r.jurisdiction_code = 'US-OK'
     and (v.approved_at is not null or v.status <> 'draft');

  if approved > 0 then
    raise exception
      'The Oklahoma pack contains % approved or non-draft rule version(s). This '
      'migration seeds drafts only; approval is a human act performed by a named '
      'reviewer against a retrieved original.', approved
      using errcode = 'check_violation';
  end if;

  select count(*) into rules
    from compliance.regulatory_rule_version v
    join compliance.regulatory_rule r on r.id = v.rule_id
   where r.jurisdiction_code = 'US-OK';

  select count(*) into tests
    from compliance.regulatory_rule_test t
    join compliance.regulatory_rule_version v on v.id = t.rule_version_id
    join compliance.regulatory_rule r on r.id = v.rule_id
   where r.jurisdiction_code = 'US-OK';

  raise notice
    'Oklahoma pack seeded: % DRAFT rule versions, % test cases, 0 approved. The '
    'engine evaluates none of them, so an Oklahoma deal reports GREEN because '
    'nothing is approved — not because it is compliant.', rules, tests;
end
$$;
