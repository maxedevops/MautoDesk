# Archived Oklahoma sources

The documents `compliance.regulatory_source_version` rows point at, kept in the
repository so that "this rule was written against this text" is a claim anyone
can check by re-hashing the file.

## What these are

**Primary text, archived verbatim** — the Service Oklahoma press release, the
11-page dealer FAQ including its page furniture and the 23 August 2024 correction
notice, and the Electronic Titles page with its full FAQ.

An earlier revision of the pack cited AI-authored consolidations of these
documents, and said so in every source row. Those are gone. With the primary text
available, citing a paraphrase would be a choice rather than a limitation.

`RegulatoryRuleTests.Every_archived_source_hashes_to_what_was_recorded` re-hashes
every file here on each build, so an edit after the fact turns the build red.

## Chain of custody

**Two different custodies live in this directory, and the difference matters.**

The three Service Oklahoma documents were transcribed from the page and PDF and
supplied by the project owner. They were not fetched from `oklahoma.gov` by this
repository's tooling. They read as verbatim — press-release voice, PDF line-wrapping, direct
quotation of the FAQ's own questions — and agree with what an independent web
search returned for the same documents.

The two enrolled bills were retrieved from `oklegislature.gov` on 13 November
2026 and extracted from the official enrolled PDFs, so each file is the bill's own
text — page furniture, amendatory strike-through artefacts and all. Those
artefacts ("without unless an inspection has been conducted") are what enrolled
amendatory text looks like and are deliberately not tidied.

The two tax statutes were retrieved from the Oklahoma State Courts Network, which
publishes the statutes as currently in force. Each file is the section plus its
Historical Data block, so the amendment chain is visible; OSCN's own navigation
and its Citationizer tables are omitted.

The holiday file is a **transcription** of two published HTML tables rather than a
capture of the page, which is a copy of evidence rather than the evidence.

A reviewer should still open the URL on the matching `compliance.regulatory_source`
row and confirm the published text has not changed since. The Electronic Titles
page in particular is a living page.

## The rules still are not approved

Every rule citing these files is `status = 'draft'` with `approved_at IS NULL`,
and the engine evaluates none of them. The **source** bar is met; the **review**
bar is not, and that is now the only thing in the way.

Each rule's `approval_note` quotes the sentence it was drawn from, so a reviewer
checks a rule against its source rather than against somebody's summary. Three
notes are flagged `INTERPRETATION TO CONFIRM`, where the rule reads something the
text does not state outright:

| Rule | The interpretation |
| --- | --- |
| `OK-PREREG-001` | The text says "all vehicles (new or used) purchased in the state of Oklahoma" with no wholesale carve-out; the rule restricts itself to retail and consignment |
| `OK-PLATE-TRANSFER-001` | The text says a dealer "should" affix a transferred plate; the rule is a warning rather than a block |
| `OK-TITLE-VIN-INSPECTION-001` | The text holds the *slip-in title request*; whether an outstanding inspection should also stop contracting is not something the source decides |

Two more are recorded as `NOT CHECKED BY THIS RULE` — temporary tags coming from
a commission-approved vendor, and whether an Electronic Title Bill of Sale
actually bears a notarisation. Both need machinery that does not exist yet.

## Before approving

1. Open each URL and confirm the published text still matches the archived file.
   If it has changed, **append** a new `regulatory_source_version` — the table is
   append-only, and the old row is the record of what the drafting used.
2. Have dealer-compliance counsel read each rule against the archived text,
   settling the three flagged interpretations.
3. Populate `compliance.jurisdiction_holiday` for `US-OK` from a cited source.
   `OK-PREREG-001` carries a business-day deadline and the engine refuses to
   compute one without a calendar.
4. Insert the approved reading as a **new** version row carrying `approved_at`
   and `approved_by`. It cannot be done by updating a draft: the table has
   `app.deny_mutation()` on UPDATE and DELETE, so no row's approval columns can
   ever be changed after it is written. An earlier revision of this file said
   otherwise.

## Files

| File | Document | Dated |
| --- | --- | --- |
| `service-ok-pre-registration-release.md` | Service Oklahoma newsroom release on the SB2035 pre-registration process | 3 September 2024 |
| `service-ok-sb2035-dealer-faq.md` | *SB2035 FAQ for Motor Vehicle Dealers* | Updated 23 August 2024 |
| `service-ok-electronic-titles.md` | *Electronic Titles* page and FAQ | Effective 1 July 2025 |
| `ok-hb3982-2026-enrolled.txt` | Enrolled H.B. 3982 — temporary license plates | Effective 1 November 2026 |
| `ok-hb3148-2026-enrolled.txt` | Enrolled H.B. 3148 — VIN inspections | Effective 1 November 2026 |
| `ok-sos-state-holidays-2026-2027.md` | Secretary of State *State Holidays*, 2026 and 2027 | Page modified 24 September 2026 |
| `ok-68-os-2103-excise-tax.txt` | 68 O.S. § 2103, *Tax for Transfer of Vehicle Ownership* | Amended eff. 1 July 2025 |
| `ok-68-os-2104-vehicle-value.txt` | 68 O.S. § 2104, *Value of Vehicles* | Amended eff. 1 July 2026 |

## Verifying a hash

```bash
sha256sum db/sources/us-ok/service-ok-sb2035-dealer-faq.md
```

Compare against `content_hash` on the matching `regulatory_source_version` row.
