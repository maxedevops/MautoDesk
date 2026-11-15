# MautoDesk — Handoff

**State:** Phases 1–10 of 13 complete, plus MFA recovery codes, wired-up
inventory writes, photo uploads, audit-ledger writes, PII log redaction, the
DealerOS compliance/lending/titling schema, API idempotency, the outbox
dispatcher, the deal and regulatory engines, the GRAY state, banded tax rates,
and both approval paths · backend green (278 unit, 21 architecture), frontend
unit green (95) · zero known dependency vulnerabilities

**This document describes `main`.** As of 2026-11-15 everything in it is merged:
[#1](https://github.com/maxedevops/MautoDesk/pull/1) — the DealerOS schema, both
engines, the Oklahoma packs and the first working CI workflow — and
[#2](https://github.com/maxedevops/MautoDesk/pull/2) — the approval path, the
archived tax statutes and the scoped counsel engagement — landed as `f7a69e3`
and `28af9c4`. No branch holds work this document does not cover.

> **One piece of history worth carrying.** CI had **never once executed** in this
> repository before #1: a quoting corruption meant the workflow did not parse, so
> an unbroken row of green-looking runs was the absence of any run at all. Any
> run older than November 2026 did not run.
> [`docs/12-dealeros-handoff.md`](docs/12-dealeros-handoff.md) is the record of
> how that work landed and of the defects it fixed on the way; this document is
> the current state.

---

## 1. What this is

A multi-tenant SaaS dealership management system for independent used-car
dealers. `docs/00-constitution.md` is the governing specification; every
subsequent phase document derives from it.

The **Inventory vertical slice runs end to end**: PostgreSQL → ASP.NET Core API →
Next.js, with real authentication. The **deal and regulatory engines** are built
and reachable through endpoints, but compute nothing today because no rule set is
approved. Everything else is designed, not built — §3 is the inventory.

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

And so does the approval role (V0014), which you only need if you are running
`rulekeeper`:

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_rulekeeper with password 'devpw';"
```

**Three roles, no passwords in any migration.** Each is created with `login` and
nothing else, deliberately: a credential in `db/migrations/*.sql` is a credential
in git. `db/tests/rulekeeper_grants.sql` asserts the third one can approve a rule
and do nothing else at all — it cannot read a deal, a vehicle or a customer, and
it holds no UPDATE or DELETE anywhere.

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
| Database | 17 migrations, RLS enabled *and forced* on every tenant table, 0 coverage gaps, hash-chained audit ledger |
| Backend | 21 projects. SharedKernel, shared Infrastructure, API host, Inventory (4), Identity (4), Sales (5, including the `taxkeeper` CLI), Compliance (5, including `rulekeeper`) |
| Inventory | Vehicles, VIN decode (NHTSA), photos (quarantine-first upload, verified and re-encoded), costs schema, status lifecycle, publish rules, outbox |
| Identity | Argon2id, mandatory TOTP MFA, single-use recovery codes, JWT, refresh rotation with reuse detection, exponential lockout |
| Frontend | Inventory grid, vehicle detail, add-vehicle form, photo upload and gallery, status changes and publish (all wired to the API), login (3-step, plus recovery-code sign-in), recovery-code settings, BFF session, generated API client, design tokens |
| Deal engine | Pure, deterministic, 25-step pipeline; three lender payment methods; banded and flat tax rates; refuses an unapproved rule set |
| Regulatory engine | Pure rule resolver and three-valued evaluator; PASS/WARNING/BLOCK/REVIEW/**UNKNOWN**; GREEN/YELLOW/**GRAY**/RED; reproducible input and result hashes |
| Deals | Create, read, list, status transitions; `POST /deals/{id}/calculate` writes an append-only snapshot; `POST /deals/{id}/evaluate` writes a compliance run |
| Idempotency | `Idempotency-Key` on `POST`: replay, 409 on key reuse, release on 5xx, 24 h retention |
| Outbox | Dispatcher drains `app.outbox_message` as a least-privileged role; lease, backoff, dead-letter |
| Approval path | `rulekeeper` (`V0014`) for a rule version, `taxkeeper` (`V0017`) for a tax rule set. Each holds its own database identity, runs ten or eleven mechanical checks, then writes one `INSERT` plus its audit event in a single transaction |
| Approval register | `compliance.pending_approval` (`V0017`): 30 items awaiting a named reviewer, with the question stated and an empty slot for the answer. `rulekeeper --register` is the work list |
| Contracts | `contracts/openapi.json` **generated** from the endpoints; drift fails the build |
| CI | 8 jobs: secrets, database, contract, design tokens, backend, frontend, e2e, CodeQL — CodeQL is a 3-way matrix, so 10 job runs and 11 required checks |

### Not built — designed only

Costs UI · CRM · the lender matrix · titles, liens, tags and pre-registration ·
documents · e-signature · Deal Guardian · OCR · messaging · marketplace
publishing · reporting · billing · Terraform · every deal and compliance screen
in the frontend.

**The Oklahoma pack is 10 DRAFT rule versions across 9 rules, 0 approved, 41 test
cases green.** `V0012` drafts six rules against 2024–25 Service Oklahoma guidance;
`V0013` drafts four more against the enrolled HB 3982 and HB 3148, which both took
effect on 1 November 2026 and made the first pack stale before anybody read it.
Every version is unapproved, so the engine evaluates none of them. **An Oklahoma
deal returns GREEN because nothing is approved, not because it is lawful.**

`db/sources/us-ok/` holds the **primary text** — the Service Oklahoma press
release, the 11-page dealer FAQ, the Electronic Titles page and FAQ, both enrolled
bills, the Secretary of State holiday calendar, and 68 O.S. §§ 2103–2104 — each
archived verbatim and hashed. `RegulatoryRuleTests` re-hashes every file on each
build, so "this rule was written against this text" is checkable rather than
asserted. Each rule's `approval_note` quotes the sentence it came from. The README
distinguishes three custodies: documents transcribed by the project owner, bills
retrieved from `oklegislature.gov`, and statutes retrieved from OSCN.

`compliance.jurisdiction_holiday` is **done** — 22 `US-OK` rows for 2026 and 2027,
each citing its source version. 2028 is absent on purpose, so a business-day
deadline falling there still fails closed.

What remains is **review**, not sourcing or machinery. Seven readings are flagged
for counsel — three in `V0012`, four in `V0013` — plus two questions `V0013`
leaves open because the statute does not answer them. An approval is recorded by
`rulekeeper` as a **new version row**; it cannot be done by editing an existing
one, because the table is append-only.
[`docs/15-counsel-engagement-brief.md`](docs/15-counsel-engagement-brief.md) is the
scoped engagement, and `db/sources/us-ok/README.md` is the source checklist.

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

**No regulatory rule is approved, and only a lawyer can change that.** Two
Oklahoma packs are drafted and nothing in either is active, so a deal reports
GREEN because nothing has been approved rather than because it is lawful. The
mechanism now exists — `rulekeeper` inserts an approved version under its own
database identity, after eleven checks, with an audit entry
([`docs/17-approval-path.md`](docs/17-approval-path.md)). What remains is an
Oklahoma-licensed attorney to review the readings and be named;
[`docs/15-counsel-engagement-brief.md`](docs/15-counsel-engagement-brief.md) is
the scoped engagement and an enquiry ready to send.

**Tax rule sets are unapproved skeletons, and research has started.** OK/KS/TX
are seeded with `'UNVERIFIED'` placeholders and `approved_at IS NULL`, so the deal
engine refuses to price a deal. This is deliberate — shipping plausible-looking
tax numbers sourced from nothing is how a DMS puts a wrong figure on a signed
retail contract.

`V0015` archives 68 O.S. §§ 2103–2104; `V0016` drafts the excise tax from them and
adds no number of its own. [`docs/16`](docs/16-oklahoma-tax-research.md) is the
field-by-field matrix: **seven fields answered by archived primary text, nine
still open**, each named with the source that would answer it, and each its own
register item so it can be closed independently.

The two engine changes that matrix asked for are **built** — banded rates and
dollar rounding, so Oklahoma's $20-plus-3.25% is expressible and a $20,000 car
prices at $621. A third surfaced while building them and is **not** done: the same
statute levies a flat 3.25% on *new* vehicles, and one rule set row per
jurisdiction-and-date cannot hold both. The drafted row covers used vehicles,
which is this product's market; how new vehicles are expressed is an open design
question recorded on the register item.

`taxkeeper` can record a reviewer's approval, and refuses a row whose
engine-read fields are still `UNVERIFIED` — so the drafted row is correctly
unapprovable until the nine fields land. Approval is a CPA's or tax counsel's,
separately from the compliance pack and against different sources.

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
| **A missing fact is GRAY, not a pass** | Every operator except `exists`/`not_exists` used to return false on an absent fact, so a rule over a field nobody captured did not fire, produced no finding, and the deal came back GREEN — a half-filled deal was indistinguishable from a lawful one. Conditions are three-valued as of `RuleEvaluator` 2.0.0: an undecidable rule is `Unknown`, the run is `Gray`, and `missingFacts` names the fields. **GREEN now means "decided on complete facts", which is a narrower claim than it was.** `docs/18` |
| **A banded tax rate is not a percentage** | Oklahoma levies $20 on the first $1,500 of value plus 3.25% of the remainder, and rounds the tax to the nearest dollar while every other line rounds to the cent. `TaxRuleSet.StateRate` could not express it at all, so `schema_version` 2 adds `bands` and `taxRounding`. A flat band amount is charged in full the moment the base reaches the band — "or less" means a $900 car owes the whole twenty dollars, not a prorated twelve |
| **A rule set carrying bands *and* a flat rate is refused** | That is two taxes, and picking one would be a guess about money on a contract. `TaxRuleSet.BandError` holds the one definition of a coherent band set; the engine and `taxkeeper` both consult it rather than each implementing it |
| **A business-day calendar with no holidays is refused** | `BusinessDayCalendar.ForJurisdiction` throws on an empty set, because a deadline computed against it would be wrong on every holiday, silently. `US-OK` is seeded for 2026–2027 only, and every other jurisdiction is still empty. Use `WithNoHolidays` to say so deliberately |
| **An INSERT without a SELECT forks the audit chain in silence** | `audit.chain_event()` runs BEFORE INSERT **as the calling role** and reads the previous row to compute `prev_hash`. `audit.event` is FORCE RLS, so a role that may INSERT but cannot SELECT gets **zero rows rather than an error** — `prev_hash` is NULL, the row claims to begin the chain, and `verify_chain()` reports the ledger broken at every such row. A ledger whose links are quietly absent still looks like evidence. Fixed and asserted for `mautodesk_rulekeeper`; **`mautodesk_app` has the same defect for platform-scope events**, latent only because nothing writes one yet — [`docs/17-approval-path.md`](docs/17-approval-path.md) §5 |
| **`rulekeeper` writes nothing without `--commit`** | The default behaviour of a tool that can write the law is to print what it would do. Its connection string comes from `RULEKEEPER_CONNECTION` and never an argument, so a credential does not land in shell history or a process list, and the sign-off arrives as a **file** rather than a string |
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

**Phase 5 is still the critical path, and what it now needs is not code.** Every
mechanical precondition is built and verified; two humans are missing.

1. **Retain an Oklahoma-licensed attorney.** This is procurement, and nothing
   regulatory downstream — phases 7, 9 and 10 — moves without it.
   [`docs/15-counsel-engagement-brief.md`](docs/15-counsel-engagement-brief.md)
   reduces it to five deliverables, a six-document reading pack and an enquiry
   ready to send.
2. **Retain a CPA or tax counsel**, separately and against different sources.
   [`docs/16-oklahoma-tax-research.md`](docs/16-oklahoma-tax-research.md) §5 lists
   the nine open fields in the order to chase them.

Three pieces of engineering are worth doing *while* that is in flight, because
each of them is on the path and none of it needs a lawyer:

- **The banded rate and dollar rounding** in the tax engine (§5). Oklahoma's
  excise tax cannot be written down until these exist.
- **Decide the `mautodesk_app` platform-scope audit chain** — a narrow SELECT
  policy, a `SECURITY DEFINER` chain function, or a dedicated identity per writer.
  It reverses a deliberate `V0006` choice either way, which is why a migration
  should not decide it. [`docs/17-approval-path.md`](docs/17-approval-path.md) §5.
- **The GRAY/UNKNOWN evaluation state.** Every operator except
  `exists`/`not_exists` returns false on an absent fact, so a rule over a fact
  nobody captured does not fire, produces no result, and reads as GREEN. An
  incompletely entered deal is today indistinguishable from a compliant one, and
  that is structural rather than per-rule.

Phase 11 (performance) still has **nothing deployed and nothing measured**, so it
would be speculation; Phase 12 (deployment) would give it an environment, and
would unblock TLS, DAST and the `⬜` rows in the FTC Safeguards table. MFA
recovery codes, the inert UI, photos, audit writes, log redaction and the
approval path — which used to head this list — are done.

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
| `docs/12-dealeros-handoff.md` | How the DealerOS work landed: what it contains, what CI verified, and the defects it fixed on the way. Historical now that it is merged, but the defect and gotcha tables are still worth reading |
| `docs/13-oklahoma-counsel-review.md` | The counsel review packet: the drafted rules in English, their quoted sources, and what the approval gate enforces |
| `docs/14-oklahoma-review-disposition-2026-11-13.md` | What the first review returned, the two 2026 statutes it surfaced, and what is still blocked on whom |
| `docs/15-counsel-engagement-brief.md` | Engaging Oklahoma counsel: the scope, the reading pack, acceptance criteria, and an enquiry to send |
| `docs/16-oklahoma-tax-research.md` | The tax field matrix: every rule-set field, its source, and the two engine changes this surfaced |
| `docs/17-approval-path.md` | How an approval is recorded, what `rulekeeper` checks first, and why it is a tool rather than an endpoint |
| `docs/18-gray-state-and-tax-bands.md` | The GRAY state, banded tax rates, the approval register and `taxkeeper`: what each fixed, and what each deliberately did not |
| `CLAUDE.md` | Working agreements and hard rules for anyone changing the code |
