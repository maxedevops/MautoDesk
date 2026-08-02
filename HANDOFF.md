# MautoDesk — Handoff

**State:** Phases 1–10 of 13 complete, plus MFA recovery codes, wired-up
inventory writes, and photo uploads · backend green (79 unit, 83 integration,
8 architecture) and frontend unit green (21); the e2e suite needs a running
stack · zero known dependency vulnerabilities

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

Then the API and the web app, each needing configuration (see `.env.example`):

```bash
dotnet run --project backend/src/MautoDesk.Api
```

```bash
pnpm --dir frontend dev
```

**Port conflicts:** every compose host port is overridable —
`POSTGRES_PORT`, `VALKEY_PORT`, `MINIO_PORT`, and so on. Developers commonly run
more than one project's database.

---

## 3. What exists

| Area | State |
| --- | --- |
| Database | 9 migrations, RLS enabled *and forced* on every tenant table, 0 coverage gaps, hash-chained audit ledger |
| Backend | 14 projects. SharedKernel, shared Infrastructure, Inventory module (4), Identity module (4), API host |
| Inventory | Vehicles, VIN decode (NHTSA), photos (quarantine-first upload, verified and re-encoded), costs schema, status lifecycle, publish rules, outbox |
| Identity | Argon2id, mandatory TOTP MFA, single-use recovery codes, JWT, refresh rotation with reuse detection, exponential lockout |
| Frontend | Inventory grid, vehicle detail, add-vehicle form, photo upload and gallery, status changes and publish (all wired to the API), login (3-step, plus recovery-code sign-in), recovery-code settings, BFF session, generated API client, design tokens |
| Contracts | `contracts/openapi.json` **generated** from the endpoints; drift fails the build |
| CI | 8 jobs: secrets, database, contract, design tokens, backend, frontend, e2e, CodeQL |

### Not built — designed only

Costs UI · CRM · deals and the deal engine · documents · e-signature ·
OCR · messaging · marketplace publishing · reporting · billing · the outbox
dispatcher · Terraform.

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

- **The audit ledger is built, chained, and empty.** No handler writes to it. An
  auditor asking "who changed this price?" has no answer today.
- **No PII log redaction.** Needs to land before the CRM module writes a customer
  object anywhere near a logger.
- **No idempotency keys.** Specified in the contract; money-adjacent endpoints are
  coming.
- **No outbox dispatcher.** Messages are written correctly and transactionally;
  nothing consumes them.

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
value work is neither: it is the **audit ledger** and **PII log redaction**,
both listed above. MFA recovery codes, the inert UI, and photos — which used to
head this list — are done.

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
| `CLAUDE.md` | Working agreements and hard rules for anyone changing the code |
