# DealerOS branch — session handoff

**Branch:** `claude/dealeros-architecture-eval-c07dce`, in a git worktree at
`.claude/worktrees/dealeros-architecture-eval-c07dce`.
**Base:** `main` @ `942f142`, which stops at `V0009` — every DealerOS migration,
both engines and all of this document's subject matter exist only on this branch.
**State:** 15 commits, pushed, open as
[maxedevops/MautoDesk#1](https://github.com/maxedevops/MautoDesk/pull/1),
`MERGEABLE`/`CLEAN`, **all 10 CI jobs green** at `9414a9b`.
**Written:** 2026-08-22. **Revised:** 2026-11-14.

Read [`docs/11-dealeros-assessment.md`](11-dealeros-assessment.md) first if you
have not: it is the evaluation this branch acts on, and it explains *why* the
stack was kept rather than rewritten. `CLAUDE.md` still governs.

---

## 1. What this branch does

It takes MautoDesk from "excellent foundation, no business domain" to "the deal
and regulatory engines exist, run through real endpoints, and have an Oklahoma
rule pack waiting on counsel."

Four increments, in order:

| Increment | What landed |
| --- | --- |
| **Assessment** | `docs/11-dealeros-assessment.md` — scoring, build/retain/replace matrix, cost model, roadmap. Verdict: **retain and extend**, do not migrate to NestJS/Prisma |
| **Phase 1** | `V0010` — `compliance`, `lending`, `titling` schemas (27 tables) |
| **Phase 2** | `V0011` — dispatcher role; API idempotency; outbox dispatcher |
| **Phases 3–4** | The two pure engines: `Sales.Domain` (deal) and `Compliance.Domain` (regulatory) |
| **Wiring** | Application + Infrastructure layers, 7 deal/compliance endpoints |
| **Phase 5** | `V0012` — Oklahoma pack, 6 DRAFT rules, primary text archived and hashed |
| **CI repair** | The workflow had **never once executed** in this repository's history — a quoting corruption meant it did not parse. Fixed, then driven to green across eight runs (§6) |
| **Review** | `docs/13` the counsel packet, `docs/14` the 13 November disposition: conditional, **nothing approved** |
| **Phase 5, second pass** | `V0013` — HB 3982 and HB 3148 archived from the enrolled bills; 4 further DRAFT rules; the `US-OK` holiday calendar seeded |

19 backend projects (was 14). 25 API paths (was 18). 13 migrations (was 9).

---

## 2. Getting it running

Same as `HANDOFF.md` §2, plus one new role and one new port note.

```bash
docker compose up -d postgres && docker compose run --rm migrate
```

That now also runs `db/tests/v0010_invariants.sql` and
`db/tests/rule_pack_approval.sql`. Then set **both** role passwords — the second
one is new and the dispatcher will not start without it:

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_app with password 'devpw'; alter role mautodesk_dispatcher with password 'devpw';"
```

**MinIO images moved.** `minio/minio` and `minio/mc` were deleted from Docker
Hub, so every compose file and the CI job now use
`bitnamilegacy/minio:2025.7.23-debian-12-r5` and
`bitnamilegacy/minio-client:2025.7.21-debian-12-r3`. The data path is
`/bitnami/minio/data`, the entrypoint starts the server (so there is no
`command:`), and `minio-init` needs `MC_CONFIG_DIR` because the client runs as a
non-root user with no writable home.

**Running the tests.** The integration suite needs PostgreSQL on `55432` and
MinIO. If port 9000 is taken by another project — it was on this machine —
bring MinIO up elsewhere and point the suite at it:

```bash
MINIO_PORT=9010 MINIO_CONSOLE_PORT=9011 docker compose up -d minio minio-init
```

```bash
TEST_STORAGE_URL=http://localhost:9010 MalwareScanning__Required=false dotnet test backend/MautoDesk.sln
```

**Regenerating the API contract** after any endpoint change:

```bash
UPDATE_CONTRACT=1 dotnet test backend/tests/MautoDesk.IntegrationTests --filter "FullyQualifiedName~ContractGenerationTests"
```

---

## 3. Verified state

Everything below is from CI run
[43563996207](https://github.com/maxedevops/MautoDesk/actions/runs/43563996207)
on `9414a9b`, against a fresh database. It is no longer one machine's word for it.

| Check | Result |
| --- | --- |
| Migrations `V0001`–`V0013` | apply clean |
| `app.rls_coverage_gaps()` | **0** |
| Pre-approved rule versions in a fresh database | **0** |
| `db/tests/isolation_probe.sql` | 14 PASS |
| `db/tests/v0010_invariants.sql` | 23 PASS |
| `V0007` / `V0012` / `V0013` re-applied | idempotent |
| Release build, warnings-as-errors | 0 warnings, 0 errors |
| Architecture / unit / integration tests | **20 / 204 / 120** |
| Frontend unit tests | **95** (74 contrast, 14 api-client, 7 web) |
| Playwright end-to-end | **green — and this is the first time the job has ever run** |
| CodeQL | csharp, javascript-typescript and actions, each its own job |
| Dependency advisories | 0 |

**Read the e2e result carefully.** Until this branch, the workflow never parsed,
so no job had ever executed: the suite was not passing, it was absent. Its first
real run found a server exception on the recovery-codes page (§6), which is what
the suite exists for.

Docker was not running on the machine this session worked from, so the migrations,
the database gates and both integration suites were verified **in CI rather than
locally**. Every claim in this table has a run id behind it.

---

## 4. Decisions that would be expensive to reverse

These are additions to `HANDOFF.md` §4, not replacements.

1. **`app.managed_schema` is the tenancy registry.** `V0006` applied RLS by
   looping over a hard-coded list of twelve schemas, and `rls_coverage_gaps()`
   checked the same hard-coded list — so the three new schemas would have been
   created with **no RLS while the CI gate reported zero gaps**. The list is now a
   table that both read. *A new schema must be registered in the migration that
   creates it, then `select app.apply_tenant_rls();`.*

2. **`lending.*` is tenant-owned, not shared reference data.** Buy rates, program
   tiers and reserve arrangements are a dealer's commercial terms; publishing them
   as reference data would leak one dealership's lender agreements to a competitor
   on the same platform.

3. **The outbox dispatcher has its own database identity.** `mautodesk_app` is
   subject to the tenancy policy on `app.outbox_message` and, having no tenant in
   a background loop, sees zero rows — forever, silently. `mautodesk_dispatcher`
   can read across tenants there and reach nothing else. Neither role can do the
   other's job, and a test asserts it.

4. **Both engines are pure and static.** No database, no HTTP, no clock; the
   governing date is an input. Architecture tests fail the build on a `double` or
   a `DateTime` in either assembly, and **both guards were verified to actually
   fail** by injecting a violation and reverting.

5. **`superseded_at` on a calculation can never be set.** The immutability trigger
   raises on any update and the app role's UPDATE grant is revoked, so the `V0004`
   comment describing a prior row being "marked superseded" describes something
   the schema forbids. Which snapshot is current is expressed by
   `sales.deal.current_calculation_id` — evidence that cannot be set to something
   untrue.

6. **No lender program rules table.** Master-prompt Step 14 suggests one;
   `lender_program_version.eligibility` jsonb is the single source of truth
   instead, typed in the engine. A second home for a number is no home.

---

## 5. The Oklahoma pack — read this before touching it

**10 DRAFT rule versions across 9 rules, 0 approved, 41 test cases green.** The engine evaluates none of
them, so **an Oklahoma deal reports GREEN because nothing is approved, not because
it is lawful.**

`V0012`, drafted against 2024–25 Service Oklahoma guidance:
`OK-PREREG-001` · `OK-TEMP-TAG-001` · `OK-PLATE-TRANSFER-001` ·
`OK-TITLE-TYPE-001` · `OK-TITLE-ETBS-001` · `OK-TITLE-VIN-INSPECTION-001`

`V0013`, drafted against the enrolled 2026 bills:
`OK-TEMP-TAG-FLEET-001` · `OK-TEMP-TAG-DEALER-60-001` · `OK-REG-DEADLINE-001` ·
`OK-TITLE-VIN-INSPECTION-001` `0.3.0-draft`

`db/sources/us-ok/` holds the primary text, hashed and re-hashed by CI on every
build; each rule's `approval_note` quotes the sentence it came from. **Two
custodies live there** and the README distinguishes them: the three Service
Oklahoma documents were transcribed and supplied by the project owner, while the
two enrolled bills were retrieved from `oklegislature.gov` and are the bills' own
text, amendatory artefacts included.

### It was reviewed, and it did not pass

[`docs/14-oklahoma-review-disposition-2026-11-13.md`](14-oklahoma-review-disposition-2026-11-13.md)
is the record. Conditional: three rules withheld or rejected as drafted, three
approved subject to change, **nothing activated and `approved_by` still empty.**
The reviewer was explicitly not an Oklahoma-licensed attorney and declined to
authorise anything, so it is review input rather than the human act the gate
needs.

Its headline finding was that the pack was stale before it was ever read. **HB
3982** (temporary plates) and **HB 3148** (VIN inspections) both took effect on
**1 November 2026**, after the drafting. `V0013` is the response: `V0012` is not
edited, and the new law arrives as new effective-dated rows. That is the
append-only design doing its job for the first time.

### What is left

1. **A retained Oklahoma-licensed attorney.** Nothing can be approved without a
   name to record. This is procurement, not engineering, and it is the critical
   path.
2. **Seven flagged readings** — three in `V0012`, four in `V0013` — plus two
   questions `V0013` deliberately leaves open because the statute does not answer
   them: what a *dealer* sale between 1 November and 31 December 2026 requires,
   and whether `OK-PLATE-TRANSFER-001` has a successor at all.
3. **An approval mechanism that exists.** See §7 — there isn't one.

`compliance.jurisdiction_holiday` is **done**: 22 `US-OK` rows for 2026 and 2027,
seeded by `V0013` from the archived Secretary of State calendar, each citing its
source version. 2028 is absent on purpose, so a deadline falling there still
fails closed.

[`db/sources/us-ok/README.md`](../db/sources/us-ok/README.md) is the checklist.
CI fails if a migration ever ships an approved rule.

**Do not approve a rule to make something work.** Two tests and two CI gates exist
to stop exactly that, and `The_seeded_oklahoma_rule_set_refuses_to_price_a_deal`
going red is the system working.

---

## 6. Defects found and fixed — worth knowing about

Each was found by a test or by reading, not by guessing, and each is the kind of
thing that would have been expensive later.

| Defect | Why it mattered |
| --- | --- |
| **`Money`/`Percentage` did not round-trip through JSON** | Get-only structs with private constructors serialise as JSON *numbers* and deserialise to **zero** — a snapshot storing correct figures and reading back as a deal worth nothing. Also violated the money-as-string rule inside the audit record. Fixed with converters in `SnapshotSerialization.Options` |
| **Compliance evaluations were never audited** | `IAuditLog.Record` only adds to the change tracker; the compliance handler owned no unit of work, so every entry was silently discarded — in the one ledger that exists to be trusted. Run and audit entry now commit together |
| **`connection.BeginTransactionAsync()` behind EF's back** | Starts a transaction on a connection the context owns but knows nothing about. Use `Database.BeginTransactionAsync()`, and `GetDbTransaction()` for raw commands. Almost certainly the intermittent-test cause |
| **"The latest run" ≠ "the run I just wrote"** | The handler read back the newest run for the deal — ambiguous on a timestamp tie, wrong when two people evaluate the same deal at once. Reads by id now |
| **EF ordered deal-before-calculation** | The `current_calculation_id` UPDATE was emitted before the INSERT it points at. `DealConfiguration` declares the relationship purely for write ordering |
| **`required` on a request DTO → 500, not 400** | The deserialiser throws, the exception handler clears the response, and the security headers go with it. Validate in the mapping method and return a 422 naming the field |
| **`'\x…'` bytea literal lost its first byte** | Tooling that interprets backslashes eats the escape, producing a hash subtly wrong rather than obviously missing. Use `decode('…','hex')`. The archived-source hash test caught it |
| **`UPDATE_CONTRACT=1` wrote to the wrong repository** | `RepositoryRoot()` looked for a `.git` *directory*; a worktree's `.git` is a *file*, so it walked past and regenerated the contract into the main checkout. Fixed to accept either — **and the main checkout was restored** |
| **`EndpointCoverageTests` silently skipped new routes** | It substituted named path parameters only, so `{dealId}` routes were never probed. Any `{…Id}` now gets a GUID |
| **`V0012` doubled its test corpus on re-apply** | No conflict clause on the rule-test insert. Unique index on `(rule_version_id, name)` |
| **The workflow had never parsed, so CI had never run** | `ports: [''5432:5432'']` — doubled single quotes in four places in the `e2e` job. Every run back to August was a startup failure: 0 jobs, 0 seconds, and a green-looking absence of failures. Also present on `main` |
| **An archived statute's hash depended on the checkout platform** | The recorded digests were of CRLF working copies; CI checks out LF, so the provenance test would pass on Windows and fail everywhere else — for the wrong reason either way. Fixed by pinning the bytes with `.gitattributes -text`, not by patching the digests |
| **The recovery-codes page threw a server exception** | `takeRecoveryCodes()` deleted the stash cookie *while rendering*, which Next.js forbids outside a Server Action or Route Handler. A user enrolling in MFA got an error page instead of the only display of their codes — and the delete consumed the stash on the way out. Split into read and discard, behind an explicit acknowledgement |
| **CodeQL was passed `language`, not `languages`** | Not an input the action has, so it was discarded with a warning and autodetect ran instead — analysing csharp, javascript *and* actions while the job name claimed only javascript-typescript. Renaming the input alone would have *narrowed* coverage to one language and silenced the warning; the matrix now declares all three |
| **A compliance run could 500 on a foreign key** | `US-ZZ` was seeded only by one test, so others passed only if that test had run first. It worked on an accumulated local database and never on a fresh one. Fixed the fixture *and* the product: an uncovered jurisdiction is now a 422 that says so |
| **An unbounded `pnpm.overrides` range crossed a major** | `"js-yaml": ">=4.3.2"` resolved to 5.5.0, which no longer exports `types.merge`; `@redocly/openapi-core` threw and client generation died. My own bug, fixed by bounding every override to its major |

---

## 7. Gotchas this session added

On top of `HANDOFF.md` §6.

| Gotcha | Detail |
| --- | --- |
| **The append-only tables refuse teardown** | A trigger blocks DELETE for the superuser too. `ApiFixture` disables the three triggers for the duration of teardown and re-enables them in a `finally`. Getting this wrong aborts teardown before it reaches the vehicles, and the *next* run fails on a duplicate stock number a long way from the cause |
| **`docker cp db container:/db` twice makes `/db/db`** | Remove the directory first, or the second copy nests and every `-f /db/...` path 404s |
| **An APR is a percent, not a fraction** | `Percentage` carries the unit in the type. `FromPercent(8.99)` is 8.99%; `AsFraction` is the only route to 0.0899, and there is no implicit conversion from `decimal` |
| **A business-day calendar with no holidays is refused** | `BusinessDayCalendar.ForJurisdiction` throws on an empty set. Use `WithNoHolidays` to say so deliberately |
| **The rule-test harness approves rules in memory** | The evaluator ignores unapproved rules, which would make every draft's tests vacuously pass. The harness copies the rule with `IsApproved = true`; the database row is untouched |
| **Approving a rule is currently impossible** | `compliance.regulatory_rule_version` carries `app.deny_mutation()` on UPDATE and DELETE, so `approved_at` can never be set on a row that already exists — not by a developer, an operator, or a superuser. The intent is visible in the invariant tests, which insert rows *already approved*: an approval is a **new version row**. But no endpoint, screen or store method performs that insert, and CI forbids migrations from it. `V0012`'s header, `docs/13` and the sources README all told a reviewer to "set the three columns"; all three are corrected |
| **A missing fact reads as compliance** | Every operator except `exists`/`not_exists` returns false on an absent fact (`RuleDefinition.cs:163`), so a condition over a fact nobody captured makes the rule *not fire*, which produces no result, which reads as GREEN. Structural rather than per-rule: an incompletely entered deal is indistinguishable from a compliant one. The strongest argument for the GRAY/UNKNOWN state `docs/14` §6 asks for |
| **The e2e suite is not idempotent against a persistent database** | A retry fails with "The E2E account is already enrolled, so its TOTP secret is unknown." Fine on CI's fresh database, a trap locally. `db/tests/reset-e2e-account.sql` is the workaround |

---

## 8. What is NOT built

Nothing consumes domain events yet — `LoggingEventPublisher` records a delivery
and delivers to nothing. No endpoint *requires* an idempotency key; the mechanism
is in place and enforcing it is a per-endpoint decision. `lending.*` and
`titling.*` have schema and no code. No frontend screen for deals, desking or
compliance — the UI is six pages: login, inventory list/new/detail, recovery
codes and the root. 25 paths are implemented against the 40 in the design
contract, and the two sets are not the same shape. Deal Guardian does not
exist. Terraform, OCR, messaging, documents, e-signature and accounting are
unchanged from `main`.

Two gaps that are easy to mistake for coverage:

- **There are no federal rules at all.** The `US` jurisdiction row exists and
  holds nothing. No TILA/Reg Z, Reg B, FTC Used Car Rule, GLBA, OFAC or Form
  8300. Approving the Oklahoma pack would not make the product a compliance
  system.
- **The tax rule set is an empty skeleton, not a draft.** `sales.rule_set` for
  OK/KS/TX carries `taxBasis: 'UNVERIFIED'`, null rates, no fees and a
  `PLACEHOLDER` citation. Nobody has researched Oklahoma's tax basis, trade-in
  credit policy or doc-fee cap, so the deal engine prices nothing — and counsel
  approving the compliance rules does not change that. It is a separate approval,
  by a CPA, against sources that do not exist yet.

---

## 9. Where to pick up

Dependency order from the roadmap in `docs/11-dealeros-assessment.md` §M. **Phase
5 is still open and is the critical path** — it now needs two things that are not
code:

0. **Finish Phase 5.** Retain an Oklahoma-licensed attorney (§5), and build an
   approval path that can actually insert an approved rule version with an audit
   entry (§7). Everything regulatory downstream waits on both. While that is in
   flight, the two unanswered statutory questions in `V0013` and any Service
   Oklahoma implementation guidance on HB 3982 are the research to chase.

Then, unchanged:

1. **Phase 6 — lender matrix.** Schema exists (`V0010`); needs the eligibility
   engine, scoring and the optimizer. The optimizer must never return a structure
   a BLOCKING rule rejects — run the evaluator over each candidate before ranking.
2. **Phase 7 — titling.** Schema exists; needs title/hold/lien/tag/pre-registration
   workflows, all rule-driven. The Oklahoma pre-registration **field list** is in
   the archived dealer FAQ — VIN, year, make, model, vehicle type, plate type
   (T/P), plate number, date of sale, buyer details, dealer licence number, new or
   used — which is the shape `titling.pre_registration.submission_payload` should
   take.
3. **Phase 9 — Deal Guardian**, orchestrating over both engines. Every RED must
   cite a `compliance_result` row.
4. **Phase 10 — frontend.** Nothing in the UI touches any of this yet.

**Oklahoma state integration is answered** and needs no invention: dealers submit
through **OkCARS**, or bulk-upload by **file transfer via their DMS provider**,
with technical specifications from `DealerSupport@service.ok.gov`. Bad rows in a
file are reviewed individually rather than rejecting the file. That closes
master-prompt Step 40 — there is a supported path, so do not build a scraper and
do not invent an API.

---

## 10. Honest limitations

- **Committed, pushed, reviewed by CI, not reviewed by a human.** 15 commits,
  106 files, open as PR #1 and mergeable. No person has read the diff.
- **The intermittent test is fixed, not proven.** `Two_evaluations_of_an_unchanged_deal_agree`
  failed three times across many runs. The cause was identified (§6, rows 3–4) and
  it has not recurred in ten consecutive solution runs — but it failed rarely
  enough that ten clean runs is evidence, not proof. The test now asserts HTTP
  status before reading the body, so a recurrence will say what happened.
- **Nothing is deployed and nothing is measured.** No Terraform, no APM data, no
  load profile. Phase-1's p95 and LCP budgets remain unverified assertions.
- **The three Service Oklahoma documents were supplied by the project owner**,
  transcribed from the page and PDF rather than fetched by this repository's
  tooling. They read as verbatim and agree with an independent search, but a
  reviewer should open the URLs on the `regulatory_source` rows and confirm the
  published text has not changed — the Electronic Titles page in particular is
  living. The two enrolled bills in `V0013` have stronger provenance: they are the
  official PDFs' own text. The holiday file is a transcription of two published
  tables, which the source row says.
- **HB 3148 is cited from its enrolled text; its *effect* is not fully modelled.**
  `V0013` carries the authorised-inspector and certification changes. The
  four-dollar fee, the notification naming an inspector, and the NAICS
  rental-establishment exemption are all recorded as `NOT CHECKED BY THIS RULE`.
- **CodeQL Action v3 is deprecated in December 2026.** The v3→v4 bump changes
  default build-mode behaviour, so it wants its own change rather than riding
  along with something else.
- **`RISK-SEC-001` is still open.** No managed KMS.
