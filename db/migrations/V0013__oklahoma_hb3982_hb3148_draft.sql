--------------------------------------------------------------------------------
-- MautoDesk — V0013 Oklahoma: HB 3982 and HB 3148 (DRAFT)
--
-- ============================================================================
-- NOTHING IN THIS MIGRATION IS APPROVED, AND NOTHING IN IT IS EVALUATED.
-- ============================================================================
--
-- WHY THIS EXISTS
--
-- V0012's pack was drafted against 2024-25 Service Oklahoma guidance. Two
-- statutes took effect on 1 November 2026, after that drafting and before any
-- review: HB 3982 (temporary license plates) and HB 3148 (VIN inspections). The
-- 13 November 2026 review of the pack — docs/14-oklahoma-review-disposition-
-- 2026-11-13.md — rejected OK-TEMP-TAG-001 as drafted for exactly this reason.
--
-- V0012's rows are NOT edited. Rule versions are append-only and effective-dated,
-- so the 2024 reading stays as the record of what the system believed about a
-- 2024 deal, and the new law arrives as new rows with their own effective_from.
-- That is the whole point of the design, and this is its first real exercise.
--
-- WHAT THE RULES WERE WRITTEN AGAINST
--
-- The enrolled bills themselves, archived under db/sources/us-ok/ and hashed:
-- not a summary, not a news report, and not the AI-generated review that found
-- the problem. Where the review's reading and the enrolled text could differ,
-- the text wins.
--
--   HB 3982 § 5:  "This act shall become effective November 1, 2026."
--   HB 3982 § 40: "...the temporary license plate valid for ten (10) days for
--                 private sales, the sixty-day dealer seller issued temporary
--                 license plate of which issuance begins January 1, 2027, and
--                 the ninety-day commercial fleet temporary license plate"
--   HB 3148 § 3:  "This act shall become effective November 1, 2026."
--
-- WHAT A HUMAN MUST STILL DO
--
--   1. Settle the readings flagged INTERPRETATION TO CONFIRM below. There are
--      four, and a developer must not settle any of them.
--   2. Decide what a DEALER sale between 1 November and 31 December 2026
--      requires. HB 3982's definition calls the ten-day plate the plate "for
--      private sales", but the sixty-day dealer plate does not begin issuing
--      until 1 January 2027. Whether a dealer sale in that two-month window
--      takes the ten-day plate, or something else, is a reading of the statute
--      that this migration deliberately does NOT make. No rule here covers it.
--   3. Decide whether OK-PLATE-TRANSFER-001 has a successor. From 1 January 2027
--      subdivision (a) says that on a dealer sale "no license plate shall be
--      issued and instead the dealer seller shall issue a sixty-day temporary
--      license plate", while subdivision (3) still speaks of issuing "a temporary
--      decal to the existing license plate" where a plate was transferred. Which
--      governs a dealer sale with a transferred plate decides whether the 2024
--      affixing rule survives. No successor is drafted here, because the answer
--      is not in the text.
--
-- APPROVAL IS AN INSERT, NOT AN UPDATE
--
-- compliance.regulatory_rule_version carries app.deny_mutation() on UPDATE and
-- DELETE, so nobody — not a developer, not an operator, not a superuser — can
-- set approved_at on a row that already exists. V0012's header and an earlier
-- revision of docs/13 both described approval as setting three columns. That is
-- impossible. An approval is a NEW version row, inserted already carrying
-- approved_at and approved_by, which the exclusion constraint then holds to
-- non-overlapping effective dates against every other approved version of the
-- same rule. The drafts below stay drafts forever; the attorney's approved
-- reading becomes a further row.
--
-- There is still no endpoint or screen that performs that insert. Recorded in
-- docs/14 §7.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Authorities for the new sources: the Legislature publishes enrolled bills, the
-- Secretary of State publishes the holiday calendar. Neither is Service Oklahoma.
--------------------------------------------------------------------------------
insert into compliance.regulatory_authority (id, jurisdiction_code, code, name, website_url) values
  ('0c000000-0000-4000-8000-000000000005', 'US-OK', 'OKLEG', 'Oklahoma Legislature',
   'https://www.oklegislature.gov/'),
  ('0c000000-0000-4000-8000-000000000006', 'US-OK', 'OKSOS', 'Oklahoma Secretary of State',
   'https://oklahoma.gov/sos.html')
on conflict (jurisdiction_code, code) do nothing;

--------------------------------------------------------------------------------
-- Sources.
--------------------------------------------------------------------------------
insert into compliance.regulatory_source
  (id, authority_id, source_type, title, citation, url) values
  ('05000000-0000-4000-8000-000000000004',
   '0c000000-0000-4000-8000-000000000005', 'statute',
   'Enrolled House Bill 3982 — temporary license plates',
   'Enrolled H.B. 3982, Oklahoma Legislature 2025-26 session, approved by the Governor 11 May 2026, effective 1 November 2026',
   'https://www.oklegislature.gov/cf_pdf/2025-26%20ENR/hB/HB3982%20ENR.PDF'),

  ('05000000-0000-4000-8000-000000000005',
   '0c000000-0000-4000-8000-000000000005', 'statute',
   'Enrolled House Bill 3148 — vehicle identification number inspections',
   'Enrolled H.B. 3148, Oklahoma Legislature 2025-26 session, approved by the Governor 11 May 2026, effective 1 November 2026',
   'https://www.oklegislature.gov/cf_pdf/2025-26%20ENR/hB/HB3148%20ENR.PDF'),

  ('05000000-0000-4000-8000-000000000006',
   '0c000000-0000-4000-8000-000000000006', 'published_guidance',
   'State Holidays',
   'Oklahoma Secretary of State, State Holidays, 2026 and 2027 calendars, page last modified 24 September 2026',
   'https://oklahoma.gov/sos/about/state-holidays.html')
on conflict (authority_id, citation) do nothing;

--------------------------------------------------------------------------------
-- Source versions.
--
-- Chain of custody differs from V0012's, and the difference matters. V0012's
-- documents were transcribed and supplied by the project owner. These two bill
-- texts were retrieved from oklegislature.gov on 13 November 2026 and extracted
-- from the official enrolled PDFs, so each archived file is the bill's own text
-- including its page furniture and its amendatory artefacts — "without unless an
-- inspection has been conducted", "the an inspection may shall be conducted" —
-- which is what enrolled amendatory text looks like, and is left in rather than
-- tidied.
--
-- The holiday file is a TRANSCRIPTION of two published HTML tables, not a
-- byte-for-byte capture of the page. Said here because the distinction is the
-- difference between evidence and a copy of evidence.
--------------------------------------------------------------------------------
insert into compliance.regulatory_source_version
  (id, source_id, version, retrieved_at, retrieved_by, content_hash, content_location,
   effective_from, change_summary, last_verified_at)
values
  ('05100000-0000-4000-8000-000000000004',
   '05000000-0000-4000-8000-000000000004', 1,
   timestamptz '2026-11-13 00:00:00+00',
   'Retrieved from oklegislature.gov by repository tooling — NOT a legal review',
   decode('ce7fa808f256f70938a0f8a9f7df15a705013b2de1c9b47b45523062b37d6944', 'hex'),
   'db/sources/us-ok/ok-hb3982-2026-enrolled.txt',
   date '2026-11-01',
   'Primary text: enrolled HB 3982 as signed, extracted from the official PDF. '
   'Carries the three temporary-plate definitions, the 1 January 2027 '
   'commencement of the sixty-day dealer plate, and the two-month registration '
   'application period.',
   timestamptz '2026-11-13 00:00:00+00'),

  ('05100000-0000-4000-8000-000000000005',
   '05000000-0000-4000-8000-000000000005', 1,
   timestamptz '2026-11-13 00:00:00+00',
   'Retrieved from oklegislature.gov by repository tooling — NOT a legal review',
   decode('4bd55157df1e19a26f81cfc859df47f663c53b556446f330ef983eb10eaefe73', 'hex'),
   'db/sources/us-ok/ok-hb3148-2026-enrolled.txt',
   date '2026-11-01',
   'Primary text: enrolled HB 3148 as signed, extracted from the official PDF. '
   'Subsection L carries the out-of-state inspection requirement, its exceptions '
   'and the four-dollar fee; subsection M carries who may conduct an inspection '
   'and the certification and notification duties.',
   timestamptz '2026-11-13 00:00:00+00'),

  ('05100000-0000-4000-8000-000000000006',
   '05000000-0000-4000-8000-000000000006', 1,
   timestamptz '2026-11-13 00:00:00+00',
   'Transcribed from the published page by repository tooling — NOT a legal review',
   decode('fd7e9ae515806941ba2fdbe67edb6de9e1c0867b719f929ce7a7e95cef058d05', 'hex'),
   'db/sources/us-ok/ok-sos-state-holidays-2026-2027.md',
   date '2026-01-01',
   'Transcription of the Secretary of State 2026 and 2027 holiday tables. Two '
   'days each are published as "Thanksgiving" and two as "Christmas"; those are '
   'the publication''s own labels and are kept rather than renamed. Only 2026 and '
   '2027 are published, so a business-day deadline falling in 2028 has no '
   'calendar and must fail closed rather than extrapolate.',
   timestamptz '2026-11-13 00:00:00+00')
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- The holiday calendar.
--
-- OK-PREREG-001's two-business-day deadline cannot be computed without this;
-- BusinessDays refuses rather than guessing. Dates are stored, not names, and
-- every row cites the source version it came from. 2028 is absent on purpose:
-- the Secretary of State has not published it, and last year's dates are not
-- evidence of next year's.
--------------------------------------------------------------------------------
insert into compliance.jurisdiction_holiday
  (jurisdiction_code, holiday_on, name, source_version_id) values
  ('US-OK', date '2026-01-01', 'New Year''s Day',             '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-01-19', 'Martin Luther King, Jr. Day', '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-02-16', 'President''s Day',            '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-05-25', 'Memorial Day',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-07-03', 'Independence Day',            '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-09-07', 'Labor Day',                   '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-11-11', 'Veterans Day',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-11-26', 'Thanksgiving',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-11-27', 'Thanksgiving',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-12-24', 'Christmas',                   '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2026-12-25', 'Christmas',                   '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-01-01', 'New Year''s Day',             '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-01-18', 'Martin Luther King, Jr. Day', '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-02-15', 'President''s Day',            '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-05-31', 'Memorial Day',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-07-05', 'Independence Day (Observed)',  '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-09-06', 'Labor Day',                   '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-11-11', 'Veterans Day',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-11-25', 'Thanksgiving',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-11-26', 'Thanksgiving',                '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-12-23', 'Christmas',                   '05100000-0000-4000-8000-000000000006'),
  ('US-OK', date '2027-12-24', 'Christmas',                   '05100000-0000-4000-8000-000000000006')
on conflict (jurisdiction_code, holiday_on) do nothing;

--------------------------------------------------------------------------------
-- New rules.
--
-- The ninety-day fleet tag and the sixty-day dealer tag are separate RULES, not
-- further versions of OK-TEMP-TAG-001, because they are separate requirements
-- that coexist rather than one requirement whose wording changed. Versioning them
-- onto one rule_id would force mutually exclusive date ranges onto obligations
-- that overlap in time once both are in force.
--------------------------------------------------------------------------------
insert into compliance.regulatory_rule
  (id, rule_code, jurisdiction_code, category, title, description) values
  ('0a000000-0000-4000-8000-000000000007', 'OK-TEMP-TAG-FLEET-001', 'US-OK', 'temporary_tag',
   'Ninety-day commercial fleet temporary license plate',
   'A commercial fleet vehicle the purchaser attests will be titled or registered outside '
   'Oklahoma, and for which a valid Manufacturer Fleet Number is presented, takes a ninety-day '
   'commercial fleet temporary license plate issued by the dealer seller. HB 3982, effective '
   '1 November 2026.'),

  ('0a000000-0000-4000-8000-000000000008', 'OK-TEMP-TAG-DEALER-60-001', 'US-OK', 'temporary_tag',
   'Sixty-day dealer-issued temporary license plate',
   'From 1 January 2027 a vehicle purchased from a licensed Oklahoma dealer seller takes a '
   'sixty-day temporary license plate issued by the dealer at the time of purchase, and no metal '
   'plate is issued at that point. HB 3982, subparagraph effective 1 January 2027.'),

  ('0a000000-0000-4000-8000-000000000009', 'OK-REG-DEADLINE-001', 'US-OK', 'registration',
   'Full registration within two months of purchase',
   'The new owner of a motor vehicle other than a commercial fleet vehicle eligible for the '
   'ninety-day plate must apply to record the registration, and pay all taxes and fees, within '
   'two months of purchase or acquisition. HB 3982, effective 1 November 2026.')
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- Rule versions. Every one of them draft, every one of them unapproved.
--------------------------------------------------------------------------------
insert into compliance.regulatory_rule_version
  (id, rule_id, version, status, effective_from, effective_to, source_version_id, trigger_event,
   definition, severity, approval_note)
values
  -- OK-TEMP-TAG-FLEET-001 ---------------------------------------------------
  ('0b000000-0000-4000-8000-000000000007',
   '0a000000-0000-4000-8000-000000000007', '0.1.0-draft', 'draft',
   date '2026-11-01', null,
   '05100000-0000-4000-8000-000000000004', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "vehicle.isCommercialFleetVehicle", "operator": "equals", "value": "true" },
         { "field": "deal.purchaserAttestsOutOfStateRegistration", "operator": "equals", "value": "true" },
         { "field": "deal.manufacturerFleetNumberPresent", "operator": "equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "NINETY_DAY_FLEET_TAG",
                      "name": "Ninety-day commercial fleet temporary license plate" } },
       { "deadline": { "code": "NINETY_DAY_FLEET_TAG", "days": 90, "businessDays": false } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source, HB 3982: "the purchaser attests that such commercial vehicle is to be titled '
   'or registered in a state other than Oklahoma and has a valid Manufacturer Fleet Number, in '
   'such case, no license plate shall be issued and instead a ninety-day commercial fleet '
   'temporary license plate shall be issued by the dealer seller at the time of purchase upon '
   'presentation of the valid Manufacturer Fleet Number to the dealer." This subdivision carries '
   'no delayed commencement, so it is in force from the act''s effective date of 1 November 2026, '
   'unlike the sixty-day plate. '
   'NOT CHECKED BY THIS RULE: that the Manufacturer Fleet Number presented is valid. The rule can '
   'only see whether one was recorded. '
   'INTERPRETATION TO CONFIRM: this rule treats the attestation and the fleet number as conditions '
   'that make the ninety-day plate MANDATORY. Read another way the subdivision is an election the '
   'purchaser may make, in which case the severity is wrong and possibly the shape is too.'),

  -- OK-TEMP-TAG-DEALER-60-001 -----------------------------------------------
  ('0b000000-0000-4000-8000-000000000008',
   '0a000000-0000-4000-8000-000000000008', '0.1.0-draft', 'draft',
   date '2027-01-01', null,
   '05100000-0000-4000-8000-000000000004', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "deal.sellerIsLicensedOklahomaDealer", "operator": "equals", "value": "true" },
         { "field": "vehicle.isCommercialFleetVehicle", "operator": "not_equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "SIXTY_DAY_DEALER_TAG",
                      "name": "Sixty-day dealer-issued temporary license plate" } },
       { "deadline": { "code": "SIXTY_DAY_DEALER_TAG", "days": 60, "businessDays": false } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT. Source, HB 3982: "the vehicle is purchased from a licensed Oklahoma dealer seller, in '
   'such a case, no license plate shall be issued and instead the dealer seller shall issue a '
   'sixty-day temporary license plate at the time of purchase. This subparagraph shall become '
   'effective January 1, 2027." effective_from is that date rather than the act''s own 1 November '
   '2026, because the subparagraph says so. '
   'INTERPRETATION TO CONFIRM: this rule excludes a commercial fleet vehicle, on the reading that '
   'the fleet subdivision is the alternative to this one. Whether a fleet vehicle that does NOT '
   'meet the attestation and fleet-number conditions falls back to the sixty-day plate is not '
   'stated, and if it does then this exclusion is wrong.'),

  -- OK-REG-DEADLINE-001 -----------------------------------------------------
  ('0b000000-0000-4000-8000-000000000009',
   '0a000000-0000-4000-8000-000000000009', '0.1.0-draft', 'draft',
   date '2026-11-01', null,
   '05100000-0000-4000-8000-000000000004', 'DEAL_SOLD',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "vehicle.isCommercialFleetVehicle", "operator": "not_equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "FULL_REGISTRATION",
                      "name": "Application to record the registration, with all taxes and fees paid" } }
     ]
   }
   $json$::jsonb,
   'warning',
   'DRAFT. Source, HB 3982: "The new owner of a motor vehicle, other than a commercial fleet '
   'vehicle eligible for the ninety-day temporary license plate, shall, within two (2) months from '
   'the date of vehicle purchase or acquisition, make application to record the registration of '
   'the vehicle with Service Oklahoma or the licensed operator and shall pay all taxes and fees '
   'provided by law." Drafted because the 13 November 2026 review found the pack conflated a '
   'tag''s expiry with the registration deadline; they are different obligations on different '
   'clocks. '
   'ENGINE LIMITATION, DELIBERATELY NOT PAPERED OVER: this rule carries NO deadline action. The '
   'deadline vocabulary expresses a number of days and the statute says two months. Sixty days is '
   'not two months, and encoding it as sixty days would invent a deadline the statute does not '
   'set. The engine needs a calendar-month deadline before this rule can carry its own. '
   'INTERPRETATION TO CONFIRM: the duty is the new OWNER''s rather than the dealer''s, so the '
   'severity is warning — the dealership is being told to surface it, not blocked for the buyer''s '
   'omission. A reviewer may hold that a dealer-facing system should not raise it at all.'),

  -- OK-TITLE-VIN-INSPECTION-001, version 0.3.0-draft ------------------------
  -- A new version of an EXISTING rule, which is what HB 3148 actually is: the
  -- requirement did not change, the question of who may discharge it did.
  ('0b000000-0000-4000-8000-00000000000a',
   '0a000000-0000-4000-8000-000000000006', '0.3.0-draft', 'draft',
   date '2026-11-01', null,
   '05100000-0000-4000-8000-000000000005', 'TITLE_SLIP_IN_REQUESTED',
   $json$
   {
     "conditions": {
       "all": [
         { "field": "title.isOutOfState", "operator": "equals", "value": "true" },
         { "field": "vehicle.registrationType", "operator": "not_in",
           "values": ["atv_utv", "off_road_motorcycle"] },
         { "field": "vehicle.isInterstateCommerceRegistered", "operator": "not_equals", "value": "true" }
       ]
     },
     "actions": [
       { "require": { "code": "VIN_INSPECTION",
                      "name": "VIN inspection comparing the vehicle's VIN to the ownership record and recording the odometer" } },
       { "require": { "code": "VIN_INSPECTION_CERTIFICATE",
                      "name": "Inspection certified on the Service Oklahoma form, naming the authorised person who conducted it" } }
     ]
   }
   $json$::jsonb,
   'blocking',
   'DRAFT, superseding 0.2.0 for deals from 1 November 2026. Source, HB 3148 subsection M: an '
   'inspection "shall be conducted by any licensed operator or a duly authorized employee '
   'thereof" and "Such an inspection may be completed by an employee of the salvage pool, salvage '
   'disposal sale, auction, licensed dealer, licensed automotive dismantler and parts recycler, or '
   'a licensed operator" — which is the change: a licensed dealer''s own employee may now conduct '
   'it. Also subsection M: "The inspection shall be certified upon forms prescribed by Service '
   'Oklahoma. The name and other identification of the authorized person conducting the inspection '
   'shall be legibly printed or typed on the form", and the employing entity must notify Service '
   'Oklahoma of that person beforehand — which is why the certificate is a second requirement '
   'rather than an attribute of the first. '
   'The trigger stays TITLE_SLIP_IN_REQUESTED. The 13 November 2026 review confirmed that reading '
   'of Q3 and HB 3148 does not disturb it. '
   'NOT CHECKED BY THIS RULE: the four-dollar fee under subsection L, which the licensed operator '
   'or Service Oklahoma collects when the title is issued rather than the dealer at inspection; '
   'that the notification to Service Oklahoma naming the inspector was actually made; and the '
   'NAICS 532111/532112 rental-establishment exemption, which needs an industry classification the '
   'system does not hold. '
   'INTERPRETATION TO CONFIRM: subsection L opens "Except for all-terrain vehicles, utility '
   'vehicles and motorcycles used exclusively for off-road use" and then excepts interstate-'
   'commerce commercial trucks, truck-tractors, trailers and semitrailers. This rule reads all of '
   'those as outside the requirement. The sentence is long enough that a reviewer should confirm '
   'the exclusions attach where the rule assumes they do.')
on conflict (id) do nothing;

--------------------------------------------------------------------------------
-- Rule tests. Firing and non-firing for each, executed by RegulatoryRuleTests
-- against the evaluator on every build — so these are assertions, not notes.
--------------------------------------------------------------------------------
insert into compliance.regulatory_rule_test (rule_version_id, name, expectation, input)
values
  -- OK-TEMP-TAG-FLEET-001
  ('0b000000-0000-4000-8000-000000000007',
   'A fleet vehicle attested out of state with a fleet number needs the ninety-day plate', 'block',
   '{"vehicle.isCommercialFleetVehicle":"true","deal.purchaserAttestsOutOfStateRegistration":"true","deal.manufacturerFleetNumberPresent":"true"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000007',
   'The ninety-day plate on file satisfies it', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"true","deal.purchaserAttestsOutOfStateRegistration":"true","deal.manufacturerFleetNumberPresent":"true","_satisfied":["NINETY_DAY_FLEET_TAG"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000007',
   'A fleet vehicle with no out-of-state attestation is out of scope', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"true","deal.purchaserAttestsOutOfStateRegistration":"false","deal.manufacturerFleetNumberPresent":"true"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000007',
   'An attested fleet vehicle with no fleet number is out of scope', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"true","deal.purchaserAttestsOutOfStateRegistration":"true","deal.manufacturerFleetNumberPresent":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000007',
   'An ordinary retail car is out of scope', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"false","deal.purchaserAttestsOutOfStateRegistration":"true","deal.manufacturerFleetNumberPresent":"true"}'::jsonb),

  -- OK-TEMP-TAG-DEALER-60-001
  ('0b000000-0000-4000-8000-000000000008',
   'A dealer sale needs the sixty-day plate', 'block',
   '{"deal.sellerIsLicensedOklahomaDealer":"true","vehicle.isCommercialFleetVehicle":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000008',
   'The sixty-day plate on file satisfies it', 'pass',
   '{"deal.sellerIsLicensedOklahomaDealer":"true","vehicle.isCommercialFleetVehicle":"false","_satisfied":["SIXTY_DAY_DEALER_TAG"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000008',
   'A private sale is out of scope', 'pass',
   '{"deal.sellerIsLicensedOklahomaDealer":"false","vehicle.isCommercialFleetVehicle":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000008',
   'A commercial fleet vehicle takes the fleet rule instead', 'pass',
   '{"deal.sellerIsLicensedOklahomaDealer":"true","vehicle.isCommercialFleetVehicle":"true"}'::jsonb),

  -- OK-REG-DEADLINE-001
  ('0b000000-0000-4000-8000-000000000009',
   'An ordinary purchase carries the registration obligation', 'warning',
   '{"vehicle.isCommercialFleetVehicle":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-000000000009',
   'A recorded registration application satisfies it', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"false","_satisfied":["FULL_REGISTRATION"]}'::jsonb),
  ('0b000000-0000-4000-8000-000000000009',
   'A ninety-day-eligible fleet vehicle is excluded by the statute', 'pass',
   '{"vehicle.isCommercialFleetVehicle":"true"}'::jsonb),

  -- OK-TITLE-VIN-INSPECTION-001 0.3.0-draft
  ('0b000000-0000-4000-8000-00000000000a',
   'An out-of-state title needs an inspection and a certificate', 'block',
   '{"title.isOutOfState":"true","vehicle.registrationType":"automobile","vehicle.isInterstateCommerceRegistered":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-00000000000a',
   'An inspection without the certificate still blocks', 'block',
   '{"title.isOutOfState":"true","vehicle.registrationType":"automobile","vehicle.isInterstateCommerceRegistered":"false","_satisfied":["VIN_INSPECTION"]}'::jsonb),
  ('0b000000-0000-4000-8000-00000000000a',
   'An inspection certified by a named authorised person satisfies it', 'pass',
   '{"title.isOutOfState":"true","vehicle.registrationType":"automobile","vehicle.isInterstateCommerceRegistered":"false","_satisfied":["VIN_INSPECTION","VIN_INSPECTION_CERTIFICATE"]}'::jsonb),
  ('0b000000-0000-4000-8000-00000000000a',
   'An Oklahoma title is out of scope', 'pass',
   '{"title.isOutOfState":"false","vehicle.registrationType":"automobile","vehicle.isInterstateCommerceRegistered":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-00000000000a',
   'An off-road motorcycle is excepted', 'pass',
   '{"title.isOutOfState":"true","vehicle.registrationType":"off_road_motorcycle","vehicle.isInterstateCommerceRegistered":"false"}'::jsonb),
  ('0b000000-0000-4000-8000-00000000000a',
   'An interstate-commerce commercial truck is excepted', 'pass',
   '{"title.isOutOfState":"true","vehicle.registrationType":"automobile","vehicle.isInterstateCommerceRegistered":"true"}'::jsonb)
on conflict do nothing;
