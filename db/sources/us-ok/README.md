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

These were transcribed from the Service Oklahoma page and PDF and supplied by the
project owner. They were not fetched from `oklahoma.gov` by this repository's
tooling. They read as verbatim — press-release voice, PDF line-wrapping, direct
quotation of the FAQ's own questions — and agree with what an independent web
search returned for the same documents.

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
4. Set `status`, `approved_at` and `approved_by` to the reviewer's name.

## Files

| File | Document | Dated |
| --- | --- | --- |
| `service-ok-pre-registration-release.md` | Service Oklahoma newsroom release on the SB2035 pre-registration process | 3 September 2024 |
| `service-ok-sb2035-dealer-faq.md` | *SB2035 FAQ for Motor Vehicle Dealers* | Updated 23 August 2024 |
| `service-ok-electronic-titles.md` | *Electronic Titles* page and FAQ | Effective 1 July 2025 |

## Verifying a hash

```bash
sha256sum db/sources/us-ok/service-ok-sb2035-dealer-faq.md
```

Compare against `content_hash` on the matching `regulatory_source_version` row.
