# Oklahoma rule pack — review disposition, 13 November 2026

**Outcome: conditional. No rule was approved. Nothing was activated. `approved_by` remains empty.**

This records the first review of the packet in `docs/13-oklahoma-counsel-review.md`. It is the
review *record*, not an approval: no `status`, `approved_at` or `approved_by` value was changed in
`compliance.regulatory_rule_version`, and the pack remains entirely draft and invisible to the
evaluator.

---

## 1. What the reviewer was, and was not

The returned document declines to authorise anything, in its own words: it states the reviewer is
**not an Oklahoma-licensed attorney**, has not passed the Oklahoma Bar, and that the document is not
a formal legal opinion. Its own sign-off block reads `Approved by: Not yet authorized` and
`Reviewer: To be completed by the independently retained Oklahoma-licensed attorney`.

It is also an **AI-generated analysis** — the file opens with `ChatGPT said:`.

Both facts matter for this project specifically, because two standing rules apply:

- AI output is a draft and never an authority.
- Blogs, forums and AI-generated articles may inform research but **must not become the
  authoritative source for a production rule**.

So this document is treated as **high-quality review input that sets the agenda** — it found a real
and serious problem, see §4 — and not as the human act the approval gate requires. The gate is
unchanged: an approval needs a named reviewer, and `rule_pack_approval.sql` rejects a name
containing `UNVERIFIED`, `TODO` or `PLACEHOLDER`. There is nothing here to enter.

| | |
| --- | --- |
| Document | `Review Oklahoma Rules.pdf` |
| SHA-256 | `ca6701039c0d553f4e102e0bd3ad293568fffcaf48010879a24000ba19f646bc` |
| Received | 13 November 2026 |
| Disposition | Conditional review — no blanket approval, no rule activated |

---

## 2. Dispositions returned

| Rule | Returned disposition | Required action |
| --- | --- | --- |
| `OK-PREREG-001` | **Withhold** | Resolve wholesale scope and model every statutory exemption explicitly; keep the two-business-day deadline |
| `OK-TEMP-TAG-001` | **Reject as drafted** | New effective-dated versions for the 10-, 90- and 60-day processes |
| `OK-PLATE-TRANSFER-001` | **Withhold** | Implement HB 3982; model tribal-plate transferability |
| `OK-TITLE-TYPE-001` | Approve with change | Identify the block as *dealership policy*, not a statutory prohibition; expand accepted ownership documents |
| `OK-TITLE-ETBS-001` | Approve with change | Limit to transfers actually using an electronic title; keep notarisation |
| `OK-TITLE-VIN-INSPECTION-001` | Approve with change | Incorporate HB 3148 and current authorised-inspector requirements |

## 3. Answers to Q1–Q3

- **Q1 — wholesale narrowing: no.** The retail-and-consignment limitation is not established by the
  cited agency publication, which covers vehicles *purchased in Oklahoma* generally. But it does not
  follow that every dealer-to-dealer transfer is identically processed; the controlling statute and
  its transaction definitions must be read. Withhold until documented.
- **Q2 — warning, but the rule is superseded.** The "should affix" instruction is correctly a
  warning rather than a prohibition. However HB 3982 changes the underlying process, so the drafted
  rule is not approved and needs effective-dated versions.
- **Q3 — no.** An outstanding VIN inspection blocks the **slip-in title request**, which is where the
  drafted trigger already sits. A dealership may impose an earlier hold, but that must be labelled a
  business risk-control policy and never presented as an Oklahoma statutory requirement.

Q3 is the one question the pack had already answered correctly.

---

## 4. The finding that matters: the pack was stale before it was ever reviewed

The pack was drafted against 2024 and 2025 agency material. Two 2026 statutes intervene, **both
effective 1 November 2026 — twelve days before this review**.

I verified HB 3982 against the enrolled bill rather than accepting the review's account of it.
[Enrolled H.B. 3982](https://www.oklegislature.gov/cf_pdf/2025-26%20ENR/hB/HB3982%20ENR.PDF),
approved by the Governor 11 May 2026:

- **§ 5:** *"This act shall become effective November 1, 2026."*
- **Definition 40** now describes three plates: the plate *"valid for ten (10) days for private
  sales"*, the *"sixty-day dealer seller issued temporary license plate of which issuance begins
  January 1, 2027"*, and the *"ninety-day commercial fleet temporary license plate"*.
- The sixty-day dealer plate carries its own commencement: *"This subparagraph shall become
  effective January 1, 2027."*
- The ninety-day fleet plate carries **no** delayed commencement, so it is live now: it applies where
  *"the purchaser attests that such commercial vehicle is to be titled or registered in a state other
  than Oklahoma and has a valid Manufacturer Fleet Number"*.
- Separately, the new owner must apply to register *"within two (2) months from the date of vehicle
  purchase or acquisition"* — a different deadline from the tag's expiry, and one the pack does not
  model.

**Consequence for `OK-TEMP-TAG-001`:** its single universal rule — ten calendar days for every retail
and consignment sale — cannot be right across all three windows. The ten-day plate is now
characterised in the definition as the *private sale* plate; dealer sales move to sixty days from
1 January 2027; and a ninety-day fleet path already exists. **Exactly which plate a dealer sale
takes between 1 November 2026 and 31 December 2026 is a reading of the statute, and is not settled
here.** That is a question for the retained attorney, not for this repository.

**HB 3148** (VIN-inspection authorisation, also effective 1 November 2026) is corroborated by
contemporaneous reporting but **I have not read its enrolled text**, so it is recorded as a lead,
not a finding. It must be retrieved and archived before `OK-TITLE-VIN-INSPECTION-001` is revised.

The append-only, effective-dated rule-version design is what makes this survivable: the 2024 rules
are not edited. New versions are added with their own `effective_from`, and the old ones stay as the
record of what the system believed on a 2024 deal.

---

## 5. Holiday calendar — source accepted, data not

The review identifies the right authority: the Oklahoma Secretary of State's
[State Holidays](https://oklahoma.gov/sos/about/state-holidays.html) publication.

**Correction to an earlier reading of this document.** I first recorded that the review's table of
dates was corrupt, citing two entries both labelled "Christmas" on 24 and 25 December 2026 and a
misaligned 2027 table. That was wrong, and the error was mine. I have since retrieved the Secretary
of State page: it genuinely publishes two days each as "Thanksgiving" and two as "Christmas", so the
duplicate labels are faithful to the source, and the 2027 misalignment was an artefact of how I
extracted the PDF rather than anything the reviewer did. **Every one of the twenty-two dates in the
review matches the published calendar.**

The process point survives the correction, and it is why the dates were not simply copied across:
law enters this system as an archived, hashed, cited source, not as a table transcribed out of a
review document. So the page itself is now archived at
`db/sources/us-ok/ok-sos-state-holidays-2026-2027.md`, hashed in
`compliance.regulatory_source_version`, and `compliance.jurisdiction_holiday` is seeded from it in
`V0013` with every row citing that source version. 2028 is deliberately absent: the Secretary of
State has not published it, and `BusinessDays` must fail closed rather than extrapolate last year's
dates.

Implementation conditions carried over from the review, all of which match the existing design:
store the actual date rather than the name, keep the calendar per year, never extrapolate next
year's dates from last year's, and fail closed when a deadline falls outside covered years.

---

## 6. Accepted recommendation: GREEN is not enough

The review asks for three outcomes rather than two, and it is right:

| Outcome | Meaning |
| --- | --- |
| **GREEN** | Every applicable approved rule evaluated, no unresolved checks |
| **RED** | At least one applicable blocking requirement failed |
| **GRAY / UNKNOWN** | Rules missing, unapproved, outdated, or required input unavailable |

Today the engine returns GREEN when nothing is approved, and only a comment distinguishes that from
compliance. A dealer looking at a screen will not read the comment. This should become a real third
state before any UI shows a compliance result — it is the difference between "we checked" and "we
have nothing to check with", and the whole liability of the product sits on that line.

Also requested, and consistent with the existing snapshot design: the evaluation record should
preserve the rule versions, source versions, transaction date and unresolved checks.

---

## 7. What is now blocked, and on whom

1. **A retained Oklahoma-licensed attorney.** Nothing can be approved without a name to record. This
   is a procurement task, not an engineering one, and it is the critical path.
2. ~~Primary-source research~~ — **done for three of four.** Enrolled HB 3982, enrolled HB 3148 and
   the Secretary of State holiday calendar are archived, hashed and cited in `V0013`. Still
   outstanding: any Service Oklahoma *implementation guidance* on HB 3982, which is what would
   settle the November–December 2026 window below.
3. ~~New effective-dated rule versions~~ — **drafted in `V0013`**, all unapproved: a ninety-day
   commercial fleet tag (in force now), a sixty-day dealer tag (from 1 January 2027), a
   registration-deadline rule the pack was missing, and a new version of the VIN-inspection rule
   carrying HB 3148's authorised-inspector and certification requirements. Four readings are flagged
   `INTERPRETATION TO CONFIRM`, and two questions are deliberately left unanswered because the
   statute does not answer them: what a **dealer** sale between 1 November and 31 December 2026
   requires, and whether `OK-PLATE-TRANSFER-001` has a successor at all once subdivision (a) says no
   plate is issued on a dealer sale while subdivision (3) still speaks of a decal on a transferred
   plate.
4. **Then** the attorney reads `V0013` alongside `V0012`, and the revisions to the three title rules
   follow from their answers.

### Two findings from drafting the replacements

**Approving a rule is currently impossible, and the documentation said otherwise.**
`compliance.regulatory_rule_version` carries `app.deny_mutation()` on UPDATE and DELETE, so no row's
`approved_at` can ever be set after the row exists — not by a developer, not by an operator, not by
a superuser. The procedure written in V0012's header and in an earlier revision of `docs/13`
("set `status`, `approved_at` and `approved_by`") cannot be carried out. The design intent is sound
and visible in the invariant tests, which insert rows *already approved*: an approval is a **new
version row**, and the exclusion constraint then keeps approved versions from overlapping in time.
But nothing in the codebase performs that insert — there is no endpoint, no screen, no store method
— and migrations are forbidden from approving anything by CI. So the gate is not only waiting on an
attorney; it is waiting on a mechanism. Both documents are corrected, and `V0013` states the real
procedure.

**A missing fact reads as compliance.** Every operator except `exists`/`not_exists` returns false
when the fact is absent (`RuleDefinition.cs:163`), so a condition over a fact nobody captured makes
the rule *not fire*, which produces no result, which reads as GREEN. This is the same hole the
review names when it insists that "a transaction whose applicability has not yet been established"
must never silently become GREEN — and it is structural rather than per-rule: an incompletely
entered deal is indistinguishable from a compliant one. It is the strongest argument for the
GRAY/UNKNOWN state in §6, and it should be fixed at the evaluator rather than by hand-writing
`exists` guards into every rule.

Unchanged and unaffected by this review: there are still no federal rules in the system, and the tax
rule set is still an empty skeleton, so the deal engine still prices nothing. The review agrees
explicitly with keeping tax disabled, and lists the federal workstream — FTC Used Car Rule, TILA /
Reg Z, ECOA / Reg B, GLBA, Form 8300, OFAC, state dealer licensing, title/lien/odometer, advertising
— as a prerequisite before the engine is marketed as a compliance product.

The reviewer's closing recommendation is a narrowly scoped revision rather than starting over, and
suggests one additional deliverable worth having: a requirements matrix mapping each statutory
provision to a rule, effective date, transaction type, severity, source citation and test case. That
is also the artefact that would make Kansas and Texas cheap.
