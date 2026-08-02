# MautoDesk — Phase 2: System Architecture

**Status:** Draft for review · **Phase:** 2 of 13
**Inputs:** `00-constitution.md`, `01-requirements-analysis.md`
**Locked decisions from Phase 1 review:** ASP.NET Core (.NET 9) · DigitalOcean + Cloudflare ·
Launch states Oklahoma, Kansas, Texas · Deliver Phases 2–4 then scaffold.

---

## 1. Architecture at a glance

```
                          ┌──────────────────────────────────────┐
   Browser / Mobile PWA ──▶│  Cloudflare                          │
                          │  DNS · WAF · CDN · Bot Mgmt · Turnstile│
                          │  Rate limiting · TLS · R2 · Images     │
                          └───────────────┬──────────────────────┘
                                          │ (origin-locked, mTLS-ish via Tunnel/allowlist)
                    ┌─────────────────────┼─────────────────────┐
                    ▼                     ▼                     ▼
        ┌───────────────────┐  ┌────────────────────┐  ┌──────────────────┐
        │ web (Next.js SSR) │  │  api (ASP.NET Core)│  │ public-feed (API)│
        │ BFF: cookie↔token │─▶│  REST /api/v1      │  │ website+syndicate│
        └───────────────────┘  └─────────┬──────────┘  └──────────────────┘
                                         │
        ┌────────────────────────────────┼────────────────────────────────┐
        ▼                                ▼                                ▼
┌────────────────┐            ┌────────────────────┐          ┌────────────────────┐
│ PostgreSQL 16  │            │  Valkey/Redis      │          │ Cloudflare R2      │
│ DO Managed     │            │  DO Managed        │          │ (S3 API) documents │
│ RLS + outbox   │            │  cache · locks     │          │ photos · signed    │
│ + Hangfire     │            │  rate-limit counters│         │ PDFs · quarantine  │
└────────────────┘            └────────────────────┘          └────────────────────┘
        ▲                                ▲
        │                                │
┌───────┴────────────┐          ┌────────┴───────────┐        ┌────────────────────┐
│ worker (.NET)      │          │ ocr-worker (Python)│        │ clamav (sidecar)   │
│ Hangfire + outbox  │◀────────▶│ OpenCV + PaddleOCR │        │ upload scanning    │
│ email·sms·images   │  HTTP    │ stateless, no DB   │        └────────────────────┘
│ feeds·AI·reports   │          └────────────────────┘
└────────────────────┘
```

**Deployment target:** DigitalOcean App Platform (v1) → DigitalOcean Kubernetes (DOKS) at scale.
Every component is a container with no App-Platform-specific code, so the migration is a manifest
change, not a rewrite. See §11.

---

## 2. Architecture decision records

Each ADR states the decision, the alternatives rejected, and the consequences we accept. These are
the answers to D-1…D-8 from Phase 1 plus the decisions forced by the DigitalOcean/Cloudflare choice.

### ADR-0001 — Backend: ASP.NET Core 9, Clean Architecture, modular monolith

**Decision.** A single deployable API built as a **modular monolith** with Clean Architecture layers,
not microservices. Modules (Inventory, CRM, Sales, Documents, Identity, Billing…) are enforced
boundaries *inside* one process: separate projects, no project reference between sibling modules,
cross-module communication only via published contracts and domain events.

**Rejected.** Microservices from day one — distributed transactions across deal/inventory/document
would dominate the engineering budget for a team of this size, and the tenancy story gets harder,
not easier, when the boundary is a network call.

**Consequences.** Extraction to a service later is mechanical because the module already has no
inbound project references. The one intentional exception is the Python OCR worker (ADR-0003) and
the workers (ADR-0006), which are separate processes for runtime reasons.

**Why .NET here specifically:** `decimal` is a native 128-bit base-10 type — the deal engine
(ADR-0008) computes money and tax, and JavaScript's lack of a native decimal is a correctness risk
we decline to take. Plus EF Core + Npgsql, `Microsoft.AspNetCore.DataProtection`, and first-class
OpenTelemetry.

### ADR-0002 — Tenancy: PostgreSQL Row-Level Security as the enforcement backstop

**Decision.** Shared database, shared schema. Every tenant-owned table carries `tenant_id uuid not
null` and an RLS policy filtering on `current_setting('app.tenant_id')`. The application connects as
a role **without** `BYPASSRLS`. Tenant context is set once per request/job on the opened connection.

```sql
-- applied to every tenant-owned table
alter table inventory.vehicle enable row level security;
alter table inventory.vehicle force row level security;
create policy vehicle_tenant_isolation on inventory.vehicle
  using (tenant_id = current_tenant_id())
  with check (tenant_id = current_tenant_id());
```

**Rejected.** EF Core global query filters alone. They are a *convenience*, not a control: a single
`IgnoreQueryFilters()`, raw SQL query, or Dapper call bypasses them silently. We keep the EF filter
(it produces better plans and catches mistakes at dev time) but the database is the authority.

**Consequences.**
- Connection pooling must not leak tenant context. `app.tenant_id` is set with `set_config(..., true)`
  (transaction-local) inside an explicit transaction, or reset on connection return. This is
  implemented once in a `TenantConnectionInterceptor` and is **the single most security-critical
  class in the codebase** — it gets a dedicated test suite.
- Platform-admin and migration operations use a *different* connection string with a different role.
- Every endpoint gets an automated cross-tenant probe test (§10).

**Tenant resolution order (strict):** access-token claim → nothing else. Never a header, subdomain,
query parameter, or request body. Subdomains may *route*, but never *authorize*.

### ADR-0003 — OCR: separate Python worker behind a queue

**Decision.** `ocr-worker` is a Python 3.12 container running OpenCV + PaddleOCR, exposed only over
an internal HTTP contract, invoked by the .NET worker. It is **stateless**: no database access, no
storage credentials beyond a scoped, short-lived signed URL for the one object it is processing.

**Rejected.** Porting to .NET (Tesseract-class quality is materially worse on the document types we
care about); calling a hosted OCR API only (per-page cost and PII egress on driver's licenses).

**Consequences.** A second language toolchain in CI and a second security surface — accepted, and
bounded by giving the worker no ambient credentials. LLM field extraction happens back in .NET
(ADR-0004) so prompt handling, cost metering, and audit stay in one place.

### ADR-0004 — AI: one provider-agnostic service layer, drafts only

**Decision.** All model calls go through `IAiCompletionService` in a single `Modules.Ai` project.
Prompts are versioned assets in source control, not string literals in feature code. Every call
records: tenant, feature, prompt version, model ID, input/output tokens, latency, cost, and a
truncated payload hash. Per-tenant monthly quotas and hard caps are enforced **before** the call.

Default provider: Claude API (`claude-sonnet-5` for bulk generation, `claude-opus-5` for extraction
requiring judgment). Provider is configuration.

**Non-negotiable product rule.** AI output that will reach a consumer — vehicle descriptions, pricing,
customer replies — is a **draft requiring explicit human approval**, is grounded only in decoded or
dealer-entered data, and is never permitted to invent equipment or history. Advertising a feature a
car does not have is a consumer-protection violation; the architecture must make it impossible to
publish un-reviewed AI text.

### ADR-0005 — Object storage: Cloudflare R2, with a quarantine-first upload flow

**Decision.** Cloudflare R2 (S3-compatible) for photos, documents, and signed PDFs. Buckets:
`mautodesk-uploads` (quarantine, private, lifecycle-expiring), `mautodesk-media` (processed photos,
served via Cloudflare CDN), `mautodesk-docs` (private, never public, signed URLs only, versioning
on), `mautodesk-vault` (immutable signed PDFs, object-lock semantics enforced by application policy
+ deny-delete credentials).

**Rejected.** DigitalOcean Spaces — R2 has zero egress fees, native Cloudflare CDN/Images
integration, and event notifications; photo delivery is our dominant egress cost.

**Upload flow (never trust the client):**
```
client → API: request upload intent (declared type, size, sha256)
API    → client: presigned PUT to quarantine bucket, short TTL, size + content-type constrained
client → R2 (direct)
client → API: confirm upload
API    → enqueue ScanAndPromoteJob
worker: verify size ≤ cap → magic-byte sniff vs declared type → extension allowlist →
        sha256 matches declared → ClamAV scan → re-encode images (strips EXIF/GPS + any payload) →
        PDF structural validation → promote to target bucket under tenant/{id}/... → record hash
        → delete quarantine object. Any failure → reject, audit, notify.
```
Nothing user-uploaded is ever served from the application origin.

### ADR-0006 — Background work: transactional outbox + Hangfire, on PostgreSQL

**Decision.** No message broker in v1.
- **Domain events** use the **transactional outbox**: the event row is written in the same
  transaction as the state change, then a dispatcher publishes it. This is what makes "no duplicate
  data entry" (the constitution's core flow) reliable — a vehicle saved *always* eventually
  publishes, even if the publish step crashes.
- **Jobs** (email, SMS, image processing, OCR, AI, feeds, reports, cleanup) use **Hangfire with
  PostgreSQL storage** for scheduling, retries, and the dashboard.
- Handlers are **idempotent by contract** and keyed by event ID; at-least-once delivery is assumed.

**Rejected.** RabbitMQ/Kafka/managed queues — DigitalOcean has no managed broker, and self-hosting
one adds an HA problem we do not need at this scale. Postgres LISTEN/NOTIFY alone is not durable.

**Consequences.** Outbox polling adds DB load; mitigated with a partial index on undispatched rows
and a batched dispatcher. The migration path to a real broker is behind `IEventPublisher` and does
not touch feature code.

### ADR-0007 — Secrets & field-level encryption: the DigitalOcean gap, addressed explicitly

**Problem.** DigitalOcean has **no managed KMS/HSM**. Phase 1 requires envelope encryption for SSN,
driver's license number, and bank/routing numbers, plus vaulted per-tenant integration credentials.

**Decision.**
- **Secrets at rest in the platform:** DigitalOcean encrypted app-level secrets for bootstrap values
  only (DB connection, master key reference). Everything else — per-tenant OAuth tokens, Twilio
  subaccounts, feed FTP credentials — is stored in the database **encrypted**, never in plaintext
  config.
- **Envelope encryption:** a `IDataKeyProvider` abstraction. v1 implementation holds a master key set
  (versioned, `kid`-tagged) injected as a platform secret; per-record data keys are generated,
  wrapped with the master key, and stored alongside the ciphertext. AES-256-GCM with the tenant ID
  and record ID bound as additional authenticated data — so ciphertext cannot be replayed into
  another tenant's row.
- **Key rotation:** new `kid` written for new records; a background re-wrap job migrates old records.
  Rotation is a documented, rehearsed runbook, not a theory.
- **Escape hatch:** `IDataKeyProvider` is the same interface an AWS KMS or Azure Key Vault backend
  would implement. If a customer's security review demands an HSM, we swap the provider without
  touching a single feature.

**Consequence we are accepting, stated plainly:** a master key living in the platform's secret store
is weaker than a hardware-backed KMS. It is acceptable for launch given the compensating controls
(AAD binding, per-record data keys, rotation, restricted access, audit) but it is a **known finding**
that should be closed before we sell to a customer with a formal security program. Tracked as
`RISK-SEC-001`.

### ADR-0008 — Deal calculation: a pure, versioned, snapshot-producing engine

**Decision.** All money math lives in `Modules.Sales.DealEngine` — a pure library with no I/O, no
clock, and no database. It takes a `DealInput` + a `JurisdictionRuleSet` and returns a
`DealCalculation`. Every saved deal stores an **immutable snapshot** of its inputs, the rule-set
version used, and the full computed breakdown.

**Why.** Tax and fee rules change. A deal signed today must recompute *identically* during an audit
in three years. Recalculating a 2026 deal with 2029 rules is a defect, not a feature.

**Rules.**
- Money is `decimal` throughout; rounding is explicit at every step (`MidpointRounding.AwayFromZero`
  to the cent, per statutory convention) — never implicit, never `double`, ever.
- Rule sets are **data**, versioned with effective dates, seeded per jurisdiction.
- Launch jurisdictions: **Oklahoma, Kansas, Texas** — see §9.
- Test strategy: golden-file tests, one file per real worked example per state, reviewed against
  actual dealer paperwork before launch in that state.

### ADR-0009 — E-signature: build it, but specify the evidence package first

**Decision.** Build in-house per the constitution. The signature is not the feature; the **evidence
package** is. Before any code:

| Evidence element | Captured |
| --- | --- |
| Consumer consent to electronic records | Explicit affirmative action, disclosure text version, timestamp, IP — presented *before* signing, revocable |
| Signer identity & attribution | Auth method (email OTP / SMS OTP / authenticated session), identity assertion, access-code entry |
| Intent to sign | Discrete "I agree and sign" action per document, not a bulk checkbox |
| Document integrity | SHA-256 of the document *before* signing, and of the completed PDF; both recorded in the audit chain |
| Association | Signature bound to a specific document version ID, not a document family |
| Environment | UTC timestamps, IP, user agent, device fingerprint, geolocation (only with consent) |
| Retention & copy | Consumer can download and retain; a copy is delivered to their email |
| Tamper evidence | Final PDF flattened, hashed, hash-chained into the immutable audit ledger, stored in the write-once vault bucket |

**Consequence.** RFC-3161 trusted timestamping and third-party legal review are launch gates, not
post-launch improvements. Tracked as `RISK-LEGAL-001`.

### ADR-0010 — Monorepo with generated, single-source API contracts

**Decision.** One repository. The **OpenAPI document generated from the .NET API is the single
source of truth**; the TypeScript client and Zod schemas are code-generated from it in CI. A drift
between backend and frontend types becomes a failing build, not a runtime bug.

### ADR-0011 — Schema: SQL-first migrations, EF Core maps to them

**Decision.** Hand-authored forward-only SQL migrations in `db/migrations/` are the single source of
truth for the database schema. EF Core maps *to* that schema via explicit
`IEntityTypeConfiguration<T>` classes and generates no migrations of its own. DbUp is the runner.

**Rejected.** EF Core migrations as the source of truth. Everything that makes this schema safe is
something EF migrations model poorly or not at all: RLS policies, `FORCE ROW LEVEL SECURITY`,
append-only triggers, GiST exclusion constraints, generated `tsvector` columns, partial and
expression indexes, and `GRANT`/`REVOKE` role separation. Generating then hand-patching produces a
file that is neither generated nor authored.

**Consequences.** Model/schema drift becomes possible, so it is closed by a test: an integration test
asserts the EF model matches the migrated database and fails the build on any mismatch. Full detail
in `docs/03-database-design.md` §1.

---

## 3. Backend structure (Clean Architecture, modular monolith)

```
backend/
  MautoDesk.sln
  src/
    MautoDesk.Api/                    # Composition root ONLY: endpoints, DI, middleware, auth
    MautoDesk.PublicApi/              # Website feed + syndication endpoints (separate surface,
                                      #   separate rate limits, anonymous-safe, no PII)
    MautoDesk.Worker/                 # Hangfire host + outbox dispatcher + job handlers
    MautoDesk.SharedKernel/           # Entity/AggregateRoot, Result<T>, Money, DomainEvent,
                                      #   TenantId, Clock abstraction. NO framework dependencies.
    Modules/
      Identity/                       # tenants, users, roles, permissions, MFA, sessions, invites
      Inventory/                      # vehicles, VIN, costs, recon, photos, status, timeline
      Crm/                            # customers, leads, tasks, notes, activity
      Sales/                          # quotes, deals, trades, deal engine, jurisdiction rules
      Documents/                      # storage, versions, templates, generation, deal jackets
      Signatures/                     # envelopes, signers, evidence package, vault
      Ai/                             # IAiCompletionService, prompts, quotas, cost metering
      Ocr/                            # orchestration + LLM extraction (Python worker is external)
      Messaging/                      # email, SMS, threads, templates, 10DLC onboarding
      Publishing/                     # website feed, marketplace feed generation + delivery
      Reporting/                      # read-model queries, exports
      Billing/                        # subscriptions, plan limits, metering
      Integrations/                   # provider registry, adapters, credential vault, circuit breakers
      Platform/                       # platform-admin, tenant provisioning, impersonation
  tests/
    <Module>.UnitTests/  <Module>.IntegrationTests/  MautoDesk.Api.ContractTests/
    MautoDesk.SecurityTests/          # cross-tenant probes, authz matrix, header assertions
    MautoDesk.ArchitectureTests/      # NetArchTest: enforces the module boundaries below
```

**Each module has the same four projects:**

| Project | Contains | May reference |
| --- | --- | --- |
| `X.Domain` | Entities, value objects, domain events, invariants. No EF, no HTTP, no DI. | SharedKernel |
| `X.Application` | Commands, queries, handlers, validators, port interfaces, authorization policies | X.Domain, X.Contracts |
| `X.Infrastructure` | EF configurations, repositories, adapters, external clients | X.Application |
| `X.Contracts` | **Public** DTOs and integration events other modules may consume | SharedKernel |

**Enforced by `MautoDesk.ArchitectureTests` (a failing test, not a code review comment):**
- No module references another module's `Domain`, `Application`, or `Infrastructure` — only
  `X.Contracts`.
- `Domain` references nothing but `SharedKernel`.
- `Api` references no `Infrastructure` type directly except in DI registration.
- No `System.DateTime.Now/UtcNow` outside the `IClock` implementation.
- No `double`/`float` in any type reachable from `Sales.Domain`.

**CQRS, applied where it earns its keep** (per the constitution's "where beneficial"): commands go
through the domain model with full invariant enforcement; queries for lists, grids, and reports
bypass the domain and project directly to DTOs with hand-tuned SQL/Dapper. A 500-vehicle inventory
grid must never materialize 500 aggregates. Same database, different read path.

---

## 4. Request pipeline

```
Cloudflare (WAF, bot score, edge rate limit, TLS)
  → Kestrel
  → ExceptionHandler        → RFC 9457 problem+json, correlation ID, never leaks internals
  → SecurityHeaders         → CSP w/ nonce, HSTS, X-Content-Type-Options, Referrer-Policy,
                              Permissions-Policy, COOP/CORP
  → CorrelationId           → accepts CF-Ray, generates otherwise; flows to logs, traces, audit
  → Serilog request logging → structured, with PII destructuring policy (§7)
  → CORS                    → strict origin allowlist, credentials only for the web origin
  → RateLimiter             → composite: per-IP, per-tenant, per-user, per-endpoint-class
  → Authentication          → JWT bearer (API) / encrypted cookie (BFF)
  → TenantContext           → resolves tenant from token claim, sets AsyncLocal + DB session var
  → Authorization           → permission-based policies, deny by default
  → Idempotency             → for POST/PUT/PATCH with Idempotency-Key
  → Endpoint (Minimal API)  → validation (FluentValidation) → MediatR → handler
  → Outbox committed in the same transaction as state changes
```

**Authentication topology.** The Next.js app is a **backend-for-frontend**: the browser holds an
`HttpOnly; Secure; SameSite=Lax` session cookie; the BFF holds the access/refresh tokens
server-side and attaches the bearer token when calling the API. The browser never sees a JWT, so
XSS cannot exfiltrate one. Third-party/API consumers use bearer tokens directly.

**Token policy.** Access token 15 min, refresh token 30 days, **rotating with reuse detection** — a
replayed refresh token revokes the entire family and raises a security event. Refresh tokens are
stored hashed. Sessions are enumerable and individually revocable by the user and by an admin.

**MFA is mandatory**, not optional, for every user with access to customer information — this is an
FTC Safeguards requirement, so it is a platform policy, not a tenant setting. TOTP at launch,
WebAuthn/passkeys designed for.

---

## 5. Authorization model

Three-level model — coarse enough to explain to a dealer, precise enough to pass a security review.

1. **Permissions** — the atoms: `inventory.vehicle.read`, `inventory.vehicle.write`,
   `inventory.cost.read` (costs are sensitive — many dealers hide them from salespeople),
   `sales.deal.approve`, `crm.customer.pii.read`, `admin.user.manage`, `platform.tenant.impersonate`.
2. **Roles** — named bundles. System roles (Owner, Sales Manager, Salesperson, F&I, Office/Title,
   Recon, Read-Only) are seeded per tenant and **customizable**; a tenant may create its own.
3. **Scopes** — row-level constraints beyond tenancy, e.g. a Salesperson sees only *their* leads and
   deals unless granted `crm.lead.read.all`.

**Enforcement is in the Application layer**, on the command/query handler — never only on the
controller. An endpoint is one caller; a background job or an internal service call is another, and
both must be gated. Deny by default: a handler without an explicit policy fails a startup check.

**Platform admin is a separate principal type** with its own token audience, its own permissions,
mandatory reason-for-access, time-boxed impersonation, and a distinct audit stream that the tenant
can read. A dealer must be able to see when we looked at their data.

---

## 6. Data architecture summary

Detail lives in Phase 3; the architectural commitments are:

- **PostgreSQL 16** (DigitalOcean Managed, primary + standby, PITR, daily backups, **restores
  rehearsed quarterly** — an unrehearsed backup is a hope, not a control).
- **Schema-per-module** (`identity`, `inventory`, `crm`, `sales`, `documents`, `signatures`,
  `messaging`, `publishing`, `billing`, `audit`, `platform`) — reinforces module boundaries and makes
  a future extraction obvious.
- **Every tenant-owned table:** `id uuid pk`, `tenant_id uuid not null`, `created_at/created_by`,
  `updated_at/updated_by`, `deleted_at/deleted_by` (soft delete), `row_version` (optimistic
  concurrency), plus the RLS policy from ADR-0002.
- **Soft delete is not erasure.** GDPR/CCPA deletion needs a real, separate crypto-shred/purge path;
  they are different operations with different permissions.
- **Audit ledger** (`audit.event`) is append-only: `REVOKE UPDATE, DELETE` from the app role, plus a
  `BEFORE UPDATE OR DELETE` trigger that raises. Each row carries the previous row's hash —
  tamper-evident chaining, per-tenant chain.
- **Search:** `tsvector` generated columns + GIN indexes, maintained in the database, not the app.
  Trigram indexes for VIN/stock/phone partial matching. Elasticsearch is a Release-3 conversation.
- **Caching:** Valkey (DO Managed Redis). Mandatory tenant key prefix enforced by a `CacheKey` type
  that cannot be constructed without a tenant. Cache-aside with explicit invalidation on domain
  events; short TTLs as a safety net, never as the primary correctness mechanism.

---

## 7. Observability

| Concern | Implementation |
| --- | --- |
| Structured logs | Serilog → OpenTelemetry → Grafana Cloud (Loki). Every log carries tenant ID, user ID, correlation ID, trace ID |
| PII redaction | A Serilog destructuring policy + a `[Sensitive]` attribute on DTO properties. **Redaction is opt-out, not opt-in:** unknown object graphs are not serialized wholesale into logs |
| Traces | OpenTelemetry auto-instrumentation for ASP.NET Core, HttpClient, Npgsql, Redis, Hangfire |
| Metrics | RED metrics per endpoint, job queue depth and age, outbox lag, AI spend per tenant, upload scan outcomes |
| Errors | Sentry (backend + frontend), release-tagged, with PII scrubbing configured before the first event |
| Health | `/health/live` (process), `/health/ready` (DB, Redis, R2, OCR worker), `/health/startup` |
| Audit | Separate from logs. Logs are for engineers and may be dropped; audit events are records and may not |
| Alerts | Error budget burn, p99 latency, queue age, failed logins spike, cross-tenant probe failure, AI cost anomaly, backup/restore failure |

---

## 8. Frontend architecture

```
frontend/
  apps/web/            # Next.js App Router — the dealer application (BFF, auth, all modules)
  apps/site/           # (Release 2) tenant-facing public inventory site / embeddable widget
  packages/api-client/ # GENERATED from openapi.yaml — never hand-edited
  packages/ui/         # design system: primitives, tokens, dark mode, a11y-tested
  packages/config/     # eslint, tsconfig, tailwind preset
```

- **Server Components by default**; client components only where interactivity demands it.
- **Route groups mirror modules** (`(app)/inventory`, `(app)/crm`, `(app)/deals`) so code splitting
  follows the navigation the user actually performs.
- **React Query** owns server state; **Zustand** owns only genuinely global UI state (command
  palette, sidebar, active tenant) — no server data in Zustand.
- **React Hook Form + Zod**, with the Zod schemas *generated from the OpenAPI contract*, so client
  and server validate the same rules. Server-side validation remains authoritative regardless.
- **TanStack Table** with server-side pagination/sort/filter. The inventory grid is never a
  client-side filter over a full fetch.
- **Mobile-first for lot work:** VIN scan (camera + barcode), photo capture and upload, recon status,
  lead response. These get dedicated compact layouts, not squeezed desktop tables.
- **Performance budget enforced in CI:** Lighthouse CI fails the build on LCP/INP/bundle regressions.
- **Accessibility:** WCAG 2.2 AA; axe in CI plus a manual keyboard pass per module. Dark mode and a
  keyboard command palette (`⌘K`) are core, not decoration.

---

## 9. Jurisdiction rules — Oklahoma, Kansas, Texas

The deal engine (ADR-0008) is jurisdiction-agnostic; rules are versioned data. What differs across
the three launch states is significant enough that this must be data-driven from the first commit:

| Concern | OK | KS | TX |
| --- | --- | --- | --- |
| Vehicle tax basis and rate structure | State-level excise-style treatment | State + local rate that varies by taxing jurisdiction | State motor-vehicle sales tax |
| Local/county add-ons | Limited | **Yes — destination-based local rates** | Limited for motor vehicles |
| Trade-in credit against taxable amount | Differs by state | Differs by state | Differs by state |
| Documentary/processing fee | Capped or disclosure-regulated | Capped or disclosure-regulated | Capped or disclosure-regulated |
| Title, registration, lien, and inspection fees | State schedule | State schedule | State schedule |
| Required disclosure forms in the deal jacket | State-specific | State-specific | State-specific |

> ⚠️ **These cells intentionally describe *structure*, not rates.** Rates, caps, and fee schedules
> change and are jurisdiction-specific; hardcoding numbers into an architecture document is how
> stale values end up in production. In Phase 3 each state's rule set is entered as **versioned,
> effective-dated seed data with a cited source**, and each is validated against real dealer
> paperwork before that state goes live. A CPA or dealer-compliance attorney signs off per state.
> The engine must be correct; the numbers must be sourced.

**Architectural requirement this creates:** Kansas's local-rate variability means the rule set is
keyed by *taxing jurisdiction*, not just state — so the schema needs an address→jurisdiction
resolution step. Building for state-only would require a schema change three months in.

---

## 10. Security architecture — verification, not just intent

Controls map to Phase 1 §6. What matters architecturally is that each is **tested**:

| Control | Automated verification |
| --- | --- |
| Tenant isolation | `MautoDesk.SecurityTests` enumerates every route from the OpenAPI doc and calls each as tenant B using tenant A's entity IDs, asserting 404/403. A new endpoint without a probe fails the suite |
| RLS actually on | A migration test asserts every table in a tenant schema has RLS enabled *and* forced; a new table without a policy fails CI |
| Authorization matrix | Table-driven test: every (role × endpoint) pair asserted allow/deny |
| Security headers | Contract test asserting exact header values on a sample of routes |
| Upload safety | Corpus of malicious files (polyglots, EICAR, zip bombs, SVG with script, mismatched magic bytes) must all be rejected |
| Secrets | `gitleaks` in CI; no secret ever reaches the repo |
| Dependencies | Dependabot + `dotnet list package --vulnerable` + `pnpm audit`, failing on high/critical |
| SAST | CodeQL (C# + TS) on every PR |
| DAST | OWASP ZAP baseline scan against the staging environment nightly |
| Pen test | Annual third-party test, plus biannual vulnerability assessment — an FTC Safeguards requirement, so it is scheduled, not aspirational |

**Cloudflare's role is a control, not just a CDN:** WAF managed rules, bot management on auth and
lead-form endpoints, Turnstile instead of CAPTCHA (we never solve CAPTCHAs, and neither should our
users be punished with them), edge rate limiting ahead of origin, and origin lock-down so the
DigitalOcean origin accepts traffic only from Cloudflare.

---

## 11. Deployment topology

| Environment | Purpose | Data |
| --- | --- | --- |
| `dev` | Local Docker Compose — full stack, seeded | Synthetic |
| `test` | Ephemeral per-PR; migrations + integration + E2E | Synthetic |
| `staging` | Production-shaped, DO App Platform | Synthetic + anonymized. **Never production PII** |
| `production` | DO App Platform → DOKS at scale | Real |

**Components deployed per environment:** `web`, `api`, `public-api`, `worker`, `ocr-worker`,
`clamav`; plus DO Managed PostgreSQL, DO Managed Valkey, Cloudflare R2 + CDN + WAF.

**Pipeline (GitHub Actions).** lint → build → unit → integration (Testcontainers: real Postgres,
real Valkey, MinIO for R2) → architecture tests → security tests → CodeQL + gitleaks → container
build + SBOM + image scan → deploy staging → migrate → E2E (Playwright) → ZAP baseline → manual gate
→ deploy production → migrate → smoke → auto-rollback on health failure.

**Migrations are expand/contract and backward-compatible for one release** — deploy never requires
downtime, and a rollback never strands the database ahead of the code. Destructive changes are a
separate, explicitly approved migration in a later release.

**IaC:** Terraform for DigitalOcean and Cloudflare. The infrastructure is reviewed in PRs like any
other code.

---

## 12. Technical debt register (opened now, reviewed at every phase gate)

| ID | Item | Trigger to address |
| --- | --- | --- |
| `RISK-SEC-001` | No hardware-backed KMS (ADR-0007) | Before the first customer security review |
| `RISK-LEGAL-001` | E-signature evidence package needs attorney review + RFC-3161 timestamping | Before GA |
| `RISK-LEGAL-002` | DL scanning/retention rules vary by state | Before the OCR module ships |
| `TD-001` | Outbox polling instead of a broker | When outbox lag p99 > 5 s sustained |
| `TD-002` | PostgreSQL FTS instead of a search engine | When search p95 > 300 ms or fuzzy quality complaints |
| `TD-003` | App Platform instead of DOKS | When we need per-component autoscaling or private networking control |
| `TD-004` | Marketplace syndication is feed-based, not API-based (Phase 1 §8) | Only if a partner program becomes available |
| `TD-005` | Single-region deployment | When a customer requires an RTO better than 4 h |

---

## 13. What Phase 3 must deliver

1. Full DDL for every schema, with RLS policies, indexes, and constraints.
2. The audit ledger with its hash chain and append-only enforcement.
3. Jurisdiction rule-set tables keyed to *taxing jurisdiction*, not state (§9).
4. The outbox table and its dispatch index.
5. Entity-relationship diagrams per module.
6. Index strategy justified against the actual query patterns from §3's CQRS read paths.
7. Seed data: system roles and permissions, the OK/KS/TX rule-set skeletons, plan definitions.
