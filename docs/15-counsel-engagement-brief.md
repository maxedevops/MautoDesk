# Engaging Oklahoma dealer-compliance counsel

**This is the one blocker nobody can code around.** Ten regulatory rules are drafted against
archived primary text, the approval mechanism now exists, the holiday calendar is loaded, and every
mechanical precondition is checked automatically. What remains is a licensed human reading the rules
against the statutes and putting their name to the result.

This document exists so that is a short, well-scoped piece of work rather than an open-ended one.

---

## 1. The ask, in one paragraph

> We have drafted ten Oklahoma motor-vehicle compliance rules for a dealership management system,
> each written against archived primary text with the governing sentence quoted alongside it. We need
> an Oklahoma-licensed attorney with dealer regulatory experience to confirm or correct each reading,
> settle seven questions we have deliberately left open, and provide a named sign-off we record
> against each rule. The research is done and the sources are archived; we are asking for review and
> judgement, not discovery.

## 2. Scope of work

**Deliverable 1 — a disposition for each of ten rule versions.** Approve as drafted, approve with a
change (stating the change and the sentence it comes from), or reject. The ten are listed in
[`docs/13-oklahoma-counsel-review.md`](13-oklahoma-counsel-review.md) §3 and
[`docs/14-oklahoma-review-disposition-2026-11-13.md`](14-oklahoma-review-disposition-2026-11-13.md)
§2.

**Deliverable 2 — written answers to seven flagged readings.** Each is marked
`INTERPRETATION TO CONFIRM` in the migration, with the text it was drawn from quoted beside it. Three
are in `V0012`, four in `V0013`.

**Deliverable 3 — two statutory questions we will not answer ourselves**, because the text does not:

1. What does a **dealer** sale between 1 November and 31 December 2026 require? HB 3982's definition
   calls the ten-day plate the plate "for private sales", but the sixty-day dealer plate "begins
   issuing" 1 January 2027.
2. Does `OK-PLATE-TRANSFER-001` have a successor at all? From 1 January 2027 subdivision (a) says
   that on a dealer sale "no license plate shall be issued and instead the dealer seller shall issue
   a sixty-day temporary license plate", while subdivision (3) still speaks of issuing "a temporary
   decal to the existing license plate" where a plate was transferred.

**Deliverable 4 — confirmation the archived sources are still current.** Five documents, each with a
URL and a recorded hash. The Service Oklahoma Electronic Titles page is a living page.

**Deliverable 5 — the reviewer's name**, as it should be recorded in `approved_by` on each approved
rule, and a signed note per rule saying what was reviewed and on what basis.

**Optional but worth asking about — ongoing legislative monitoring.** Two statutes took effect on
1 November 2026 and invalidated a rule pack that had been drafted correctly against 2024–25 guidance
and never activated. A rule that was accurate when drafted can be wrong before it is ever switched
on. A standing arrangement to be told when Oklahoma amends Title 47 or Title 68 vehicle provisions is
worth more than the first review.

## 3. What is *not* being asked

Worth stating plainly, because it changes the quote and the risk:

- **Not a compliance opinion for any dealership.** No dealer is a client here, no transaction is
  being reviewed, and no deal has been priced or processed. The subject is software behaviour.
- **Not tax.** Oklahoma vehicle excise tax, trade-in treatment, title and registration fees and
  documentation-fee limits are a separate workstream needing a CPA or tax counsel. The engine refuses
  to price a deal today and will keep refusing until that is done separately. See
  [`docs/16-oklahoma-tax-research.md`](16-oklahoma-tax-research.md).
- **Not federal.** TILA/Regulation Z, Regulation B, the FTC Used Car Rule, GLBA, OFAC and Form 8300
  are unmodelled and acknowledged as such. They are a later engagement, not part of this one.
- **Not a certification that the product is compliant.** Reviewing ten rules does not make a
  dealership compliant, and the system is built to say so: a GREEN result where nothing is approved
  means "nothing was checked", not "this is lawful".

## 4. The reading pack

Everything needed, in reading order. It is self-contained; no access to the codebase is required
beyond these files, and nothing in it assumes the reader writes software.

| # | File | What it is |
| --- | --- | --- |
| 1 | `docs/13-oklahoma-counsel-review.md` | The packet: each rule in plain English with its quoted source, and what the approval gate enforces |
| 2 | `docs/14-oklahoma-review-disposition-2026-11-13.md` | A prior non-attorney review, its findings, and the two 2026 statutes it surfaced |
| 3 | `db/sources/us-ok/` | The archived primary text — five documents, hashed |
| 4 | `db/migrations/V0012__oklahoma_rule_pack_draft.sql` | The six 2024–25 rules, each with its `approval_note` quoting its source |
| 5 | `db/migrations/V0013__oklahoma_hb3982_hb3148_draft.sql` | The four 2026 rules drafted against the enrolled bills |
| 6 | `docs/17-approval-path.md` | How a sign-off is recorded, and what cannot be done afterwards |

Point 6 matters to a reviewer's exposure and is worth flagging when you make contact: **an approval
is permanent and append-only.** It cannot be edited or withdrawn. A correction is a further version
with its own effective dates, and the original stays as the record of what the system believed on a
deal written that day. That is a feature for auditability and a constraint on the reviewer, and they
should know it before they sign rather than after.

## 5. What to look for in a reviewer

- **Oklahoma-licensed**, and willing to say so in writing on the sign-off.
- **Dealer regulatory practice** — Service Oklahoma processes, titling and registration, the New
  Motor Vehicle Commission and the Used Motor Vehicle, Dismantler and Manufactured Housing
  Commission. A general commercial attorney will spend your money learning this.
- **Comfortable reviewing a specification rather than a transaction.** The question is always "does
  this rule state the requirement correctly", not "is this deal lawful". Some litigators find that an
  odd exercise; regulatory counsel usually do not.
- **Willing to be named in a database.** Some will not be, and that is a legitimate position — but it
  is the whole deliverable, so establish it in the first conversation rather than the last.

## 6. Where to look

Channels, not recommendations — nobody here has vetted any particular firm:

- **The National Association of Dealer Counsel** publishes an annual attorney directory organised by
  state. It is the most direct route to lawyers whose practice is specifically dealership regulatory
  work.
- **The Oklahoma Independent Automobile Dealers Association** (`okiada.com`) runs dealer education,
  including training on SB 2035, and will know who its members use.
- **The two state commissions** — the Oklahoma New Motor Vehicle Commission and the Oklahoma Used
  Motor Vehicle, Dismantler and Manufactured Housing Commission — publish dealer compliance material
  and are worth reading before the first call, so the conversation starts informed.
- **The Oklahoma Bar Association** operates a lawyer referral service for a general starting point.

## 7. Commercial shape

A suggestion rather than a requirement, offered because the obvious framing — hourly, open-ended — is
the one most likely to stall:

- **A fixed fee for the first pack**, scoped as the five deliverables in §2. Ten rules, five archived
  documents, seven flagged readings, two statutory questions.
- **Per-rule pricing for later packs**, since Kansas and Texas are the same exercise against
  different statutes, and the process will be familiar by then.
- **A small standing retainer for legislative monitoring**, if they offer it.

## 8. Acceptance criteria

The engagement is complete when, for each of the ten rule versions, there is:

1. A disposition — approve, approve with a stated change, or reject.
2. A written note saying what was reviewed and on what basis, long enough to be a review. The tool
   that records approvals refuses a note under forty characters, which is a floor and not a target.
3. The reviewer's name as it should appear in `approved_by`.

Plus the seven flagged readings answered, the two statutory questions answered, and the five archived
sources confirmed current.

A rule approved with a change comes back as a **new draft** for review, not as an edit to the
existing one. That is one more round trip, and it is the mechanism that keeps the history of what was
proposed separate from the history of what was approved.

## 9. A first enquiry you can send

> Subject: Oklahoma dealer compliance review — ten drafted rules, fixed scope
>
> We are building a dealership management system for independent used-car dealers and are looking for
> an Oklahoma-licensed attorney with dealer regulatory experience to review a defined set of
> compliance rules.
>
> The work is scoped and the research is done. We have ten rules covering vehicle pre-registration,
> temporary tags, plate transfer, title type, the Electronic Title Bill of Sale and VIN inspections,
> each drafted against archived primary text — Service Oklahoma guidance plus the enrolled HB 3982 and
> HB 3148 — with the governing sentence quoted next to each rule. We need each reading confirmed or
> corrected, seven questions we have deliberately left open settled, and a named sign-off we record
> against each rule.
>
> Two things to flag up front. We are not asking for a compliance opinion on any dealership or
> transaction; the subject is whether our software states each requirement correctly. And an approval
> is recorded permanently and append-only in our system — it cannot be edited or withdrawn later, only
> superseded by a further version — so we want a reviewer comfortable with that before we begin.
>
> Tax, federal consumer-credit and privacy requirements are out of scope for this engagement and
> handled separately.
>
> The reading pack is six documents and self-contained. Could we send it over, and would you be able
> to quote a fixed fee for the review?
