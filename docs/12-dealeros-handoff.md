# DealerOS branch — session handoff

**Branch:** `claude/dealeros-architecture-eval-c07dce`, in a git worktree at
`.claude/worktrees/dealeros-architecture-eval-c07dce`.
**Base:** `main` @ `942f142`. **Nothing is committed** — 48 changed paths, 26 of
them new, all in the working tree.
**Written:** 2026-08-22.

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

19 backend projects (was 14). 25 API paths (was 18). 12 migrations (was 9).

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

Everything below was run from a **fresh** database on 2026-08-22, in the order CI
uses.

| Check | Result |
| --- | --- |
| Migrations `V0001`–`V0012` | apply clean |
| `app.rls_coverage_gaps()` | **0** |
| Pre-approved rule versions in a fresh database | **0** |
| `db/tests/isolation_probe.sql` | 14 PASS |
| `db/tests/v0010_invariants.sql` | 23 PASS |
| `V0012` re-applied | idempotent |
| Release build, warnings-as-errors | 0 warnings, 0 errors |
| Architecture / unit / integration tests | **20 / 204 / 119** |

The full solution suite was run **ten consecutive times** while chasing an
intermittent failure (§6). The last four runs after the final fix were clean.

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

**6 DRAFT rules, 0 approved, 23 test cases green.** The engine evaluates none of
them, so **an Oklahoma deal reports GREEN because nothing is approved, not because
it is lawful.**

`OK-PREREG-001` · `OK-TEMP-TAG-001` · `OK-PLATE-TRANSFER-001` ·
`OK-TITLE-TYPE-001` · `OK-TITLE-ETBS-001` · `OK-TITLE-VIN-INSPECTION-001`

`db/sources/us-ok/` holds the **primary text** as published by Service Oklahoma —
press release, 11-page dealer FAQ, Electronic Titles page and FAQ — archived
verbatim and hashed, re-hashed by CI on every build. Each rule's `approval_note`
quotes the sentence it came from.

**The source bar is met. The review bar is not**, and that is the only thing left:

1. Counsel settles the three notes flagged `INTERPRETATION TO CONFIRM` — whether
   pre-registration really excludes wholesale (the text says "all vehicles…" with
   no carve-out), whether a transferred plate is a block or a warning (the text
   says the dealer "should"), and whether an outstanding VIN inspection should stop
   contracting as well as the title request (the text only holds the request).
2. `compliance.jurisdiction_holiday` needs populating for `US-OK` from a cited
   source. `OK-PREREG-001` carries a business-day deadline and the engine
   **refuses** to compute one without a calendar — that is intended, not a bug.
3. A named reviewer sets `status`, `approved_at`, `approved_by`.

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

---

## 8. What is NOT built

Nothing consumes domain events yet — `LoggingEventPublisher` records a delivery
and delivers to nothing. No endpoint *requires* an idempotency key; the mechanism
is in place and enforcing it is a per-endpoint decision. `lending.*` and
`titling.*` have schema and no code. No frontend screen for deals, desking or
compliance. Deal Guardian does not exist. Terraform, OCR, messaging, documents,
e-signature, accounting: unchanged from `main`.

---

## 9. Where to pick up

Dependency order from the roadmap in `docs/11-dealeros-assessment.md` §M:

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

- **Nothing is committed.** 48 changed paths in a worktree. Review before merging.
- **The intermittent test is fixed, not proven.** `Two_evaluations_of_an_unchanged_deal_agree`
  failed three times across many runs. The cause was identified (§6, rows 3–4) and
  it has not recurred in ten consecutive solution runs — but it failed rarely
  enough that ten clean runs is evidence, not proof. The test now asserts HTTP
  status before reading the body, so a recurrence will say what happened.
- **Nothing is deployed and nothing is measured.** No Terraform, no APM data, no
  load profile. Phase-1's p95 and LCP budgets remain unverified assertions.
- **The archived Oklahoma text was supplied by the project owner**, transcribed
  from the Service Oklahoma page and PDF rather than fetched by this repository's
  tooling. It reads as verbatim and agrees with an independent web search, but a
  reviewer should open the URLs on the `regulatory_source` rows and confirm the
  published text has not changed — the Electronic Titles page in particular is
  living.
- **`RISK-SEC-001` is still open.** No managed KMS.
