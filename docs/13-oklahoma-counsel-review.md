# Oklahoma rule pack — counsel review packet

**Status: awaiting review. Nothing in this pack is active.**

This is the sign-off point for Phase 5 of the roadmap in `docs/11-dealeros-assessment.md` §M. Six
Oklahoma rules have been drafted against archived primary text. They are invisible to the software
until a named reviewer approves them, and that approval is the only thing standing between the
drafts and a working compliance engine.

A reviewer needs no SQL. Everything to be confirmed is quoted below in English, with a pointer to
the exact line of the migration it came from and to the archived source sentence it was read from.

---

## 1. The one thing to understand first

Every rule below is seeded `status = 'draft'`, `approved_at = NULL`. The evaluator filters on
approval twice — once by `RegulatoryRuleVersion.GovernsOn()`, which the evaluator filters on, and again in the store's query — so an
Oklahoma deal evaluated today comes back **GREEN**.

**GREEN currently means "no rule has been approved". It does not mean "this deal is lawful."**

That is deliberate and it is asserted by a test. It must not be relaxed to make a demo work.

---

## 2. What is being reviewed

The primary text is archived in `db/sources/us-ok/`, hashed, and re-hashed by `RegulatoryRuleTests`
on every build — so "this rule was written against this text" is checkable rather than asserted.

| Archived file | Original | SHA-256 (first 12) |
| --- | --- | --- |
| `service-ok-pre-registration-release.md` | [Service Oklahoma press release, 3 Sept 2024 (SB2035, Mason Treat Act)](https://oklahoma.gov/service/newsroom/sok-new-vehicle-pre-registration-process.html) | `f927b8c373e7` |
| `service-ok-sb2035-dealer-faq.md` | [SB2035 FAQ for Motor Vehicle Dealers, updated 23 Aug 2024](https://oklahoma.gov/content/dam/ok/en/onmvc/documents/24-035%20MV%20Groups-SB2035-FAQ%2020.pdf) | `817fa82b5be9` |
| `service-ok-electronic-titles.md` | [Service Oklahoma Electronic Titles page and FAQ, effective 1 July 2025](https://oklahoma.gov/service/all-services/auto-vehicle/electronic-titles.html) | `bc0257d575ce` |

**Chain of custody, stated plainly.** These files were transcribed from the Service Oklahoma page
and PDF and supplied by the project owner. They were **not** fetched from oklahoma.gov by this
repository's tooling, and no software in this system scrapes a government site. They read as
verbatim and agree with an independent search, but a reviewer should open the three URLs and
confirm the published text has not changed. The Electronic Titles page in particular is living; if
it has changed, a **new** source version row is appended rather than the existing row edited.

---

## 3. The six rules

Definitions are at [V0012 lines 172–226](db/migrations/V0012__oklahoma_rule_pack_draft.sql:172);
each version with its quoted source note follows at the line given.

| Rule | Plain English | Severity | Fires on | Line |
| --- | --- | --- | --- | --- |
| `OK-PREREG-001` | A retail or consignment sale of a non-exempt vehicle requires a pre-registration submission to Service Oklahoma within **two business days**. Thirteen registration types are exempt. | blocking | sale | [236](db/migrations/V0012__oklahoma_rule_pack_draft.sql:236) |
| `OK-TEMP-TAG-001` | A retail buyer who is not transferring an existing plate requires a temporary paper tag, expiring **ten calendar days** from the date of sale. | blocking | sale | [273](db/migrations/V0012__oklahoma_rule_pack_draft.sql:273) |
| `OK-PLATE-TRANSFER-001` | Where an existing plate is transferred, its details must be submitted and the plate affixed. | **warning** | sale | [301](db/migrations/V0012__oklahoma_rule_pack_draft.sql:301) |
| `OK-TITLE-TYPE-001` | A deal cannot be contracted while the title type is unknown. Dealers receive **paper** by default on an initial title transaction, and paper may also apply to auction, out-of-state, floorplan and lease. | blocking | contracting | [329](db/migrations/V0012__oklahoma_rule_pack_draft.sql:329) |
| `OK-TITLE-ETBS-001` | A used vehicle with an electronic title requires a **notarised** Electronic Title Bill of Sale. | blocking | contracting | [356](db/migrations/V0012__oklahoma_rule_pack_draft.sql:356) |
| `OK-TITLE-VIN-INSPECTION-001` | An out-of-state title requires a VIN inspection; without it a slip-in title request is held. | blocking | slip-in title request | [383](db/migrations/V0012__oklahoma_rule_pack_draft.sql:383) |

Every rule ships with a case that makes it fire and a case that does not — from
[line 425](db/migrations/V0012__oklahoma_rule_pack_draft.sql:425) — so the moment a rule is
approved its tests are already green, or already red. A rule with only a firing case has been shown
to be a constant, not a rule.

---

## 4. The questions that need a lawyer's answer

These are readings where the source is silent, broader than the rule, or permissive in a way that
is a judgement call. **A developer must not settle any of them.**

**Q1 — `OK-PREREG-001`: is the wholesale narrowing right?**
([line 264](db/migrations/V0012__oklahoma_rule_pack_draft.sql:264))
The source says the process *"applies to all vehicles (new or used) purchased in the state of
Oklahoma"*, with no wholesale carve-out. The rule restricts itself to retail and consignment deals.
That narrowing is our reading, not something the text states. The rule does correctly **not**
exclude vehicles to be registered in another state, another jurisdiction, or with a tribe, which the
source addresses expressly.

**Q2 — `OK-PLATE-TRANSFER-001`: warning or blocking?**
([line 321](db/migrations/V0012__oklahoma_rule_pack_draft.sql:321))
The text says a dealer *"should"* ensure the plate is affixed, rather than stating a prohibition, so
the rule is a warning. If that "should" carries an enforceable obligation, this becomes blocking.
Also **not modelled**: members of tribal nations other than the Chickasaw and Choctaw Nations
cannot transfer an existing tribal plate and therefore do need a temporary tag.

**Q3 — `OK-TITLE-VIN-INSPECTION-001`: should an outstanding inspection also stop contracting?**
([line 404](db/migrations/V0012__oklahoma_rule_pack_draft.sql:404))
The source puts the hold on the **slip-in title request**, so that is the trigger. Whether an
outstanding inspection should additionally block a deal being contracted is a business decision the
source does not make.

### Limits the rules declare about themselves

Not defects — scope, stated so nobody assumes coverage that is not there.

- **`OK-TEMP-TAG-001` does not check the vendor.** The source requires tags be ordered *"through
  their motor vehicle commission-approved vendors"*. That needs a vendor list the system does not
  hold.
- **`OK-TITLE-ETBS-001` does not verify the notarisation.** It requires the document; whether the
  document actually bears a notary's seal is for the document engine, which is not built.

---

## 5. Before any approval can be recorded

1. **Confirm the three URLs still publish the archived text** (§2). If a page has changed, append a
   new source version; do not edit the existing row.
2. **Populate `compliance.jurisdiction_holiday` for US-OK from a cited source.** `OK-PREREG-001`
   carries a two-business-day deadline, and business days are defined by the source as *"Mondays
   through Fridays excluding state holidays"*. The calendar is seeded empty on purpose, and
   `BusinessDays` refuses to compute a deadline rather than guessing — see
   [BusinessDays.cs:62](backend/src/Modules/Compliance/MautoDesk.Compliance.Domain/BusinessDays.cs:62).
   Approving `OK-PREREG-001` without the calendar produces a rule that cannot run.
3. **Answer Q1–Q3** above, in writing, naming the reviewer.

---

## 6. What this pack does *not* cover

Three gaps that matter more than anything inside the pack, because a reader could otherwise assume
review of these six rules makes the product compliant.

- **There are no federal rules at all.** The jurisdiction row for `US` exists
  ([line 53](db/migrations/V0012__oklahoma_rule_pack_draft.sql:53)) and holds nothing. TILA /
  Regulation Z disclosures, Regulation B, the FTC Used Car Rule, GLBA safeguards, OFAC screening and
  the Form 8300 cash-reporting threshold are **not modelled anywhere in this system**.
- **The tax rule set is an empty skeleton, not a draft.** `sales.rule_set` for OK/KS/TX is seeded
  with `taxBasis: 'UNVERIFIED'`, null rates, no fees and the citation
  `'PLACEHOLDER — must cite the governing statute or DMV publication before approval'` — see
  [V0007 line 256](db/migrations/V0007__seed.sql:256). Nobody has researched Oklahoma's tax basis,
  trade-in credit policy, doc-fee cap or state fees yet. Until that research is done and a CPA or
  tax counsel approves it, the deal engine will refuse to price any deal, and **reviewing the six
  compliance rules does not change that**. This is a separate approval, by a different reviewer,
  against different sources.
- **Only Oklahoma.** Kansas and Texas have jurisdiction rows and no rules.

---

## 7. How an approval is recorded

Approval sets three columns on `compliance.regulatory_rule_version`: `status`, `approved_at`, and
`approved_by` — the reviewer's real name.

**What the system enforces, so that an approval cannot be faked or drift into existence:**

- **No migration may ship an approved rule.** CI asserts a freshly migrated database contains zero
  approved rule versions, before any fixture runs — `.github/workflows/ci.yml:79`. A pack that
  arrives pre-approved is a pack that skipped review.
- **An approval must name a real reviewer.** `approved_by` that is blank, or contains `UNVERIFIED`,
  `TODO` or `PLACEHOLDER`, fails the build —
  [rule_pack_approval.sql](db/tests/rule_pack_approval.sql).
- **An approved rule must cite an archived source.** A rule whose source version has no
  `content_location` fails: approving against a URL is not the same as reading the law.
- **Two approved versions cannot cover the same day**, so the resolver can never pick between two
  readings of the same rule — asserted in [v0010_invariants.sql](db/tests/v0010_invariants.sql).
- **The application role cannot write the law it is judged by.** Grants are separated; the API
  cannot approve its own rules.

A developer must not perform the approval. The intended path is an operator action taken on the
reviewer's written instruction, recorded with their name — not a migration, and not a code change.

> **Note on the mechanism.** There is no approval *endpoint* or admin screen yet; the columns are
> currently set by hand, by someone with database access, against the reviewer's written sign-off.
> That is adequate for a first pack reviewed once, and it should become a permissioned endpoint with
> an audit entry before a second jurisdiction is loaded.

---

## 8. What to return

For each of the six rules: **approve as drafted**, **approve with a change** (state the change and
the sentence it comes from), or **reject**. Plus:

- Written answers to Q1, Q2 and Q3.
- A cited source for the Oklahoma state-holiday calendar.
- Confirmation that the three archived documents match what is published today.
- Your name, as it should be recorded in `approved_by`.

Anything approved with a change goes back as a **new** rule version — rule versions are
effective-dated and append-only, so a correction is a new row and the history of what the system
believed on a given date stays intact. That is what makes a disputed 2026 deal replayable in 2029.
