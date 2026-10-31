# MautoDesk — Handoff

**State:** Phases 1–10 of 13 complete, plus MFA recovery codes, wired-up
inventory writes, photo uploads, audit-ledger writes, PII log redaction, the
DealerOS compliance/lending/titling schema, API idempotency, the outbox
dispatcher, and the deal and regulatory engines · backend green (204 unit, 101
integration, 20 architecture) and frontend unit green (21); the e2e suite needs a
running stack · zero known dependency vulnerabilities

> **Checkpointed.** Phases 1–10 are in a single initial commit on `main`, pushed
> to <https://github.com/maxedevops/MautoDesk>. Work from a branch off `main`
> from here.

---

## 1. What this is

A multi-tenant SaaS dealership management system for independent used-car
dealers. `docs/00-constitution.md` is the governing specification; every
subsequent phase document derives from it.

The **Inventory vertical slice runs end to end**: PostgreSQL → ASP.NET Core API →
Next.js, with real authentication. Everything else is designed, not built.

---

## 2. Getting it running

Prerequisites: Docker, Node 24 + pnpm, .NET 9 SDK.

**The .NET SDK may not be on your PATH in a fresh shell** — it installs to
`C:\Program Files\dotnet`. Prepend it if `dotnet` is not found.

```bash
docker compose up -d postgres && docker compose run --rm migrate
```

That applies every migration, asserts `app.rls_coverage_gaps()` returns zero, and
runs the cross-tenant isolation probe. It is idempotent — it keeps a journal in
`public.schema_version` and skips what is already applied.

The application role has no password until you set one:

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_app with password 'devpw';"
```

So does the dispatcher's role, which is separate on purpose (V0011):

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_dispatcher with password 'devpw';"
```

Then the API and the web app, each needing configuration (see `.env.example`):

```bash
dotnet run --project backend/src/MautoDesk.Api
```

```bash
pnpm --dir frontend dev
```

**Deploying it**, as opposed to running it locally: `deploy/README.md` is the
runbook for a single VPS — two container images, Caddy for TLS, and the
migration job as a pre-deploy gate. The compromises that topology makes are
listed there rather than left to be discovered.

**Port conflicts:** every compose host port is overridable —
`POSTGRES_PORT`, `VALKEY_PORT`, `MINIO_PORT`, and so on. Developers commonly run
more than one project's database.

---

## 3. What exists

| Area | State |
| --- | --- |
| Database | 12 migrations, RLS enabled *and forced* on every tenant table, 0 coverage gaps, hash-chained audit ledger |
| Backend | 18 projects. SharedKernel, shared Infrastructure, Inventory (4), Identity (4), Sales (Domain + Contracts), Compliance (Domain + Contracts), API host |
| Inventory | Vehicles, VIN decode (NHTSA), photos (quarantine-first upload, verified and re-encoded), costs schema, status lifecycle, publish rules, outbox |
| Identity | Argon2id, mandatory TOTP MFA, single-use recovery codes, JWT, refresh rotation with reuse detection, exponential lockout |
| Frontend | Inventory grid, vehicle detail, add-vehicle form, photo upload and gallery, status changes and publish (all wired to the API), login (3-step, plus recovery-code sign-in), recovery-code settings, BFF session, generated API client, design tokens |
| Deal engine | Pure, deterministic, 25-step pipeline; three lender payment methods; refuses an unapproved rule set |
| Regulatory engine | Pure rule resolver and evaluator; PASS/WARNING/BLOCK/REVIEW; reproducible input and result hashes |
| Deals | Create, read, list, status transitions; `POST /deals/{id}/calculate` writes an append-only snapshot; `POST /deals/{id}/evaluate` writes a compliance run |
| Idempotency | `Idempotency-Key` on `POST`: replay, 409 on key reuse, release on 5xx, 24 h retention |
| Outbox | Dispatcher drains `app.outbox_message` as a least-privileged role; lease, backoff, dead-letter |
| Contracts | `contracts/openapi.json` **generated** from the endpoints; drift fails the build |
| CI | 8 jobs: secrets, database, contract, design tokens, backend, frontend, e2e, CodeQL |

### Not built — designed only

Costs UI · CRM · the lender matrix · the Oklahoma rule pack · titles, liens,
tags and pre-registration · documents · e-signature · Deal Guardian · OCR ·
messaging · marketplace publishing · reporting · billing · Terraform · every
deal and compliance screen in the frontend.

**The Oklahoma rule pack is six DRAFT rules and nothing approved — but the source bar is now met.** V0012 seeds
`OK-PREREG-001`, `OK-TEMP-TAG-001`, `OK-PLATE-TRANSFER-001`, `OK-TITLE-TYPE-001`,
`OK-TITLE-ETBS-001` and `OK-TITLE-VIN-INSPECTION-001` with condition trees and 23
test cases that run in CI — all `status = 'draft'` with `approved_at IS NULL`, so
the engine evaluates none of them. **An Oklahoma deal returns GREEN because nothing
is approved, not because it is lawful.**

`db/sources/us-ok/` holds the **primary text** — the Service Oklahoma press
release, the 11-page dealer FAQ and the Electronic Titles page and FAQ, archived
verbatim and hashed. `RegulatoryRuleTests` re-hashes every file on each build, so
"this rule was written against this text" is checkable rather than asserted. Each
rule's `approval_note` quotes the sentence it came from.

What remains is **review**, not sourcing. Counsel must settle the three notes
flagged `INTERPRETATION TO CONFIRM` (whether pre-registration really excludes
wholesale, whether a transferred plate is a block or a warning, whether an
outstanding VIN inspection should stop contracting as well as the title request),
`compliance.jurisdiction_holiday` needs populating for `US-OK`, and a named
reviewer sets `approved_by`. `db/sources/us-ok/README.md` is the checklist.

The majority of remaining *risk* lives here, not in what is built.

---

## 4. Decisions that would be expensive to reverse

Full reasoning in `docs/02-architecture.md` §2 (ADRs 0001–0011). The ones that
shape everything:

1. **Tenancy is enforced by PostgreSQL RLS, not by application filters.** The app
   connects as `mautodesk_app` — no `BYPASSRLS`, not the table owner. A bug in
   the data layer returns zero rows instead of another dealership's customers.
2. **The tenant comes from a signed token claim and nothing else.** There is no
   `X-Tenant-Id` header in this system and there must never be.
3. **SQL-first migrations.** `db/migrations/*.sql` is the source of truth; EF maps
   *to* it and generates nothing. RLS, triggers, exclusion constraints and
   generated columns are things EF migrations model poorly.
4. **Money is `decimal` / `numeric(14,2)` / a JSON *string*.** No float anywhere,
   enforced by an architecture test.
5. **Deal figures are immutable snapshots.** A correction is a new version.
6. **AI output is a draft.** Nothing model-generated reaches a consumer without
   explicit human approval.
7. **Modular monolith.** Modules may only reference each other's `Contracts`
   project — enforced by `MautoDesk.ArchitectureTests`, not by convention.

---

## 5. Open items, in the order I would address them

### Blocking for real users

**Tax rule sets are unapproved skeletons.** OK/KS/TX are seeded with
`'UNVERIFIED'` placeholders and `approved_at IS NULL`, so the deal engine will
refuse to price a deal. This is deliberate — shipping plausible-looking tax
numbers sourced from nothing is how a DMS puts a wrong figure on a signed retail
contract. They need primary sources and a CPA or dealer-compliance attorney per
state.

### Important, not urgent

- **No endpoint yet *requires* an idempotency key.** The mechanism is in place and
  tested; making it mandatory is a per-endpoint decision, and belongs with the
  first endpoint that submits something to a lender or the state.
- **Nothing consumes domain events yet.** The dispatcher drains the outbox and
  hands each message to `IEventPublisher`; the default implementation records the
  delivery and delivers it to nothing, because no module subscribes yet.

### Known and accepted

- `RISK-SEC-001` — DigitalOcean has no managed KMS, so the envelope-encryption
  master key lives in the platform secret store. Compensating controls are real;
  `IDataKeyProvider` makes closing it a provider swap.
- `RISK-LEGAL-001` — the e-signature evidence package needs attorney review and
  RFC-3161 timestamping before GA.
- `RISK-LEGAL-002` — driver's-licence scanning and retention rules vary by state;
  review before the OCR module ships.
- Rate limiter partitions are in-process; correct at one instance.
- CSP needs `'unsafe-inline'` for styles because Next inlines critical CSS.

---

## 6. Things that will confuse you

Discovered the hard way during the build. Each is documented at its site, but
they are the sort of thing that costs an afternoon.

| Gotcha | Detail |
| --- | --- |
| **Production rate limits break test suites** | 10 auth attempts per 15 minutes per IP is correct and hostile to anything automated from one address. Limits are configurable with **production values as defaults**; the integration fixture and CI raise them, and `RateLimitingTests` lowers one to prove the limiter fires |
| **The E2E account must start unenrolled** | The suite enrols MFA and captures the secret from the screen — it cannot know an existing one. Reset with `db/tests/reset-e2e-account.sql` or re-run `db/seed/e2e.sql` |
| **`dependencies` does not apply `storageState`** | In Playwright it only *orders* projects. Without `use.storageState` the authenticated tests run signed out and fail with "element not found" |
| **EF needs declared relationships for insert ordering** | Refresh tokens reference their session and their successor. Without `HasOne<T>().WithMany()` EF orders the writes wrongly and the foreign key rejects them |
| **EF raw SQL rejects `DBNull.Value`** | Its overload takes `IEnumerable<object>`. Use `NpgsqlParameter` instances for nullable values |
| **`SqlQueryRaw` scalars must be named `"Value"`** | EF wraps them in `select s."Value" from (…) as s` |
| **`inet` columns need a value converter** | Npgsql maps `inet` to `IPAddress`, not `string` |
| **`superseded_at` on a calculation can never be set** | The column exists and the V0004 comment describes marking a prior row superseded, but the immutability trigger raises on *any* update and the app role's UPDATE grant is revoked — so nothing can write it. Which version is current is expressed by `sales.deal.current_calculation_id` instead |
| **Money and rates need JSON converters to round-trip** | `Money` and `Percentage` are get-only structs with private constructors. Without the converters in `SnapshotSerialization.Options` they serialize as JSON *numbers* and deserialize to **zero** — a snapshot that stores the right figures and reads back as a deal worth nothing |
| **EF needs the deal→calculation relationship declared** | Saving a deal and its new snapshot together emits the `current_calculation_id` UPDATE before the INSERT it points at, and the foreign key rejects it. `DealConfiguration` declares `HasOne<DealCalculationRecord>().WithMany()` purely for write ordering |
| **`UPDATE_CONTRACT=1` used to write to the wrong repository** | `RepositoryRoot()` looked for a `.git` *directory*; in a worktree `.git` is a *file*, so it walked past and regenerated the contract into the main checkout. Fixed to accept either |
| **Compliance evaluations were never audited** | `IAuditLog.Record` only adds to the change tracker and lands when the caller saves. `ComplianceEvaluationHandler` owned no unit of work, so every entry it recorded was silently discarded. The run and its audit entry now commit in one transaction, pinned by `Evaluating_a_deal_is_recorded_in_the_audit_ledger` |
| **`connection.BeginTransactionAsync()` goes behind EF's back** | It starts a transaction on a connection the context owns but knows nothing about, so EF's next command runs inside a transaction it cannot roll back. Use `Database.BeginTransactionAsync()`, and `GetDbTransaction()` for raw commands |
| **A `'\x…'` bytea literal can lose its first byte in transit** | Tooling that interprets backslashes eats the escape, and the result is a hash that is subtly wrong rather than obviously missing. `decode('…','hex')` has no backslash. The archived-source hash test is what caught it |
| **"The latest run" is not "the run I just wrote"** | `ComplianceEvaluationHandler` used to read back the newest run for the deal, which is ambiguous when two share a timestamp and plain wrong when two people evaluate the same deal at once. It reads by id now, and `GetLatestAsync` orders by `executed_at, id` so the GET is deterministic too |
| **A `required` member on a request DTO becomes a 500, not a 400** | The JSON deserializer throws on a missing `required` member, the exception handler clears the response, and the security headers go with it. Validate in the mapping method instead and return a 422 that names the field |
| **The deal engine refuses every jurisdiction today** | Every seeded rule set is unapproved with `'UNVERIFIED'` placeholders, so `DealEngine.Calculate` returns `deal.rule_set_unapproved` for all of them. That is the designed behaviour and there is a test on it. Do not relax it to make a demo work |
| **An APR is a percent, not a fraction** | `Percentage` carries the unit in the type: `FromPercent(8.99)` is 8.99%, and `AsFraction` is the only way to the 0.0899 the formula needs. There is no implicit conversion from `decimal`, so a bare number cannot become a rate by accident |
| **A business-day calendar with no holidays is refused** | `BusinessDayCalendar.ForJurisdiction` throws on an empty set, because `compliance.jurisdiction_holiday` is seeded empty and a deadline computed against it would be wrong on every holiday, silently. Use `WithNoHolidays` to say so deliberately |
| **The dispatcher needs its own database password** | `mautodesk_dispatcher` is created by V0011 without one, exactly as `mautodesk_app` is. `alter role mautodesk_dispatcher with password 'devpw';`. Without `Outbox__ConnectionString` the API starts, logs a warning, and publishes nothing |
| **The app connection cannot drain the outbox** | `app.outbox_message` is under RLS and `mautodesk_app` has no tenant in scope in a background loop, so it sees zero rows — forever, silently. This is why there is a second role, and `OutboxDispatcherTests` asserts it |
| **`response_body` is `json`, not `jsonb`** | `jsonb` reparses, so a replayed body comes back with different whitespace than the original. Byte-identical replay is the promise; V0011 changes the column type |
| **A reverse proxy needs `Network__TrustedProxies` set** | A forwarded header is believed only from a listed peer. Behind Cloudflare you must add its public ranges, or every caller shares one rate-limit partition; with nothing in front, empty the list. `docs/06-backend.md` §11 |
| **Presigned URLs ignore the endpoint's scheme** | `GetPreSignedUrlRequest.Protocol` defaults to HTTPS regardless of `ServiceURL`, so a local MinIO gets an `https://localhost:9000` URL nothing can connect to. `S3ObjectStore` sets it from the configured endpoint |
| **Server Actions cap request bodies at 1 MB** | Which rejects essentially every photo a phone takes. `next.config.ts` raises it to 25 MB; the API's own 20 MB limit is the real one |
| **Photo tests need MinIO, not just Postgres** | `docker compose up -d minio minio-init`. Point elsewhere with `TEST_STORAGE_URL`. The suite sets `MalwareScanning__Required=false` because clamd takes three minutes to load its signatures |
| **Configuration must be set via environment for tests** | `Program.cs` uses top-level statements, so `CreateBuilder` reads config before `WebApplicationFactory` can contribute an in-memory source |
| **Integration tests do not run in parallel** | Deliberate — they share one database and process-wide environment. `AssemblyInfo.cs` disables it |
| **Two OpenAPI files, on purpose** | `openapi.json` is generated and is what the client is built from. `openapi.design.yaml` is the Phase 4 spec and still describes unbuilt endpoints |

---

## 7. Where to pick up

Phase 11 is performance optimisation, but **there is nothing deployed and nothing
measured**, so it would largely be speculation. Two sensible orders:

- **Stand up the k6 profile against local Docker first**, get real numbers, then
  optimise against them. Phase 1 sets explicit budgets (p95 < 200 ms, LCP < 2.0 s)
  that nothing currently measures.
- **Or skip to Phase 12 (deployment)** so Phase 11 has a real environment to
  measure. This also unblocks TLS, DAST, and the `⬜` rows in the FTC Safeguards
  table.

If the goal is a usable product rather than the next phase number, the highest
value work is neither: it is **idempotency keys** before money-adjacent
endpoints arrive, then the **outbox dispatcher** so the events already being
written are consumed by something. MFA recovery codes, the inert UI, photos,
audit writes, and log redaction — which used to head this list — are done.

---

## 8. Documentation map

| Document | Read it for |
| --- | --- |
| `docs/00-constitution.md` | The governing specification |
| `docs/01-requirements-analysis.md` | Scope, personas, NFRs, and an honest integration reality check |
| `docs/02-architecture.md` | System design, ADRs 0001–0011, the technical debt register |
| `docs/03-database-design.md` | Schema rationale, isolation, index strategy, retention |
| `docs/04-api-contracts.md` | Error shape, paging, idempotency, rate limits |
| `docs/05-ux-design.md` | Colour doctrine, typography, flows with click budgets, accessibility |
| `docs/06-backend.md` | Backend structure, decisions, known gaps |
| `docs/07-frontend.md` | The contract chain and how drift is caught |
| `docs/08-authentication.md` | Auth decisions, the tenant boundary at login, attack-path tests |
| `docs/09-security-review.md` | Control coverage **with evidence**, 11 findings, FTC posture |
| `docs/10-testing.md` | What each test layer buys, and what is still untested |
| `docs/11-dealeros-assessment.md` | The DealerOS assessment: scoring, build/retain/replace matrix, cost model, roadmap |
| `docs/12-dealeros-handoff.md` | **Start here on the DealerOS branch.** What it contains, what is verified, the defects it fixed, and where to pick up |
| `CLAUDE.md` | Working agreements and hard rules for anyone changing the code |
