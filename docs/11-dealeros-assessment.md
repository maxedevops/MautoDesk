# DealerOS — Existing-System Assessment and Evolution Plan

**Assessed:** 2026-08-20 · **Repository:** MautoDesk (`main` @ `942f142`) · **Scope:** evaluation
against the DealerOS master prompt (`dealeros_master_package/PROMPT.md`).

This document is an *evaluation of what exists*, followed by a decision matrix and a roadmap. It
changes no code. Nothing in it invents an Oklahoma rule, a lender program, a tax rate, or a
government API.

---

## SECTION A — Executive summary

### What exists

MautoDesk is **not a prototype and not a legacy system**. It is a deliberately-built, partially-
implemented modular monolith with an unusually strong foundation and an almost entirely unbuilt
business domain.

| | |
| --- | --- |
| Backend | ASP.NET Core 9, C#, Clean Architecture, 14 projects, **9,733 LOC** across 45 files |
| Modules implemented | **Identity** and **Inventory** only |
| Database | PostgreSQL 16, **9 SQL migrations**, 80 tables across 12 schemas, RLS enabled *and forced* |
| API surface | **20 endpoints** (auth + vehicles + photos + VIN decode) |
| Frontend | Next.js App Router, **5 routes** (login, inventory grid/detail/new, recovery codes) |
| Tests | 4,927 LOC — 99 unit, 89 integration, 8 architecture, 21 frontend, 11 Playwright e2e |
| CI | 8 gated jobs including a real-PostgreSQL RLS-coverage and cross-tenant isolation probe |
| Deployment | Docker Compose (dev / local / prod-on-one-VPS) + Caddy. **`infra/` and `ocr/` are README stubs.** |

### What is good — and should be protected, not replaced

1. **Tenant isolation is the best part of this system.** Tenancy is resolved *only* from a signed
   token claim; the app connects as `mautodesk_app`, which is neither the table owner nor holds
   `BYPASSRLS`; RLS is `ENABLE` **and** `FORCE` on every tenant-owned table; `app.rls_coverage_gaps()`
   fails CI if a new tenant table lacks a policy; the exemption list has 9 entries, each with a
   written justification, all genuinely shared reference data. `TenantConnectionInterceptor` sets and
   — critically — *resets* the session variable on connection return, with a leak test for exactly
   that path. This exceeds the master prompt's Step 7 requirement.
2. **The money model is already correct.** `Money` is `decimal`-backed, rounds half-away-from-zero
   (not banker's), has no `double` conversion, parses the wire format as a string, and an
   architecture test fails the build if `double` becomes reachable from a Sales domain type.
   Schema is `numeric(14,2)` for money and `numeric(9,6)` for rates. Steps 9 and 10 are pre-satisfied.
3. **`sales.deal_calculation` is already the snapshot table Step 13 asks for** — append-only by
   trigger, carrying `input`/`output` jsonb, `engine_version`, `rule_set_id`, `rule_set_version`, and
   a `content_hash`. No totals are denormalized onto `sales.deal`.
4. **`sales.rule_set` is already versioned, effective-dated, source-cited, and approval-gated**, with
   a GiST exclusion constraint preventing two approved rule sets from overlapping in time for one
   jurisdiction. Rules are keyed to a **taxing jurisdiction**, not a state — the correct design, and
   the one Kansas will require.
5. **`audit.event` is append-only and hash-chained**, with `audit.verify_chain()` to detect tampering
   that bypassed the application.
6. **The contract chain is closed.** `contracts/openapi.json` is *generated* from the running API and
   drift fails the build; the TypeScript client is generated from it.
7. **Architecture is enforced by tests, not convention.** `MautoDesk.ArchitectureTests` blocks
   cross-module references to anything but `X.Contracts`, and blocks infrastructure leaking into
   Domain/Application.
8. **The tax rule sets are deliberately unapproved.** `approved_at IS NULL` with `'UNVERIFIED'`
   placeholders, and the engine is specified to refuse to price a deal. Refusing to ship
   plausible-looking numbers sourced from nothing is the single most professionally correct decision
   in the repository.

### What is dangerous

Ranked by risk to a commercial launch:

| # | Risk | Why it is dangerous |
| --- | --- | --- |
| 1 | **The deal engine does not exist.** Zero lines of Sales code. | Everything DealerOS sells — Deal Guardian, lender matrix, optimizer — sits on top of it. All product risk is concentrated here. |
| 2 | **There is no regulatory engine.** `sales.rule_set` is a *tax/fee* table, not a rules engine. No `regulatory_rule`, `compliance_run`, `compliance_result`, or override tables exist. | Steps 16–19 are the product's moat and are entirely greenfield. |
| 3 | **No title / lien / temp-tag / pre-registration model at all.** Not even schema. | These are the Oklahoma-specific operational core (Steps 21–24). Nothing exists to build on. |
| 4 | **Zero Oklahoma regulatory content, verified or otherwise.** | The OK pack must be built from primary sources before any rule may be marked ACTIVE. That is research plus legal review, not coding, and it is on the critical path. |
| 5 | **No idempotency implementation.** `app.idempotency_key` exists in schema; **no code references it.** | Money-adjacent and submission endpoints (lender, pre-registration, tag issuance) are next. A retried POST that double-issues a temp tag is a regulatory incident. |
| 6 | **No outbox dispatcher.** Messages are written transactionally; nothing consumes them. | Every event-driven behaviour is silently inert. |
| 7 | **Nothing is deployed and nothing is measured.** No Terraform, no APM data, no load profile. | Phase-1 budgets (p95 < 200 ms, LCP < 2.0 s) are unverified assertions. |
| 8 | `RISK-SEC-001` — envelope-encryption master key lives in a platform secret store, no managed KMS. | Will fail a dealer's or lender's security review. `IDataKeyProvider` makes it a provider swap. |
| 9 | Rate-limiter partitions are in-process. | Correct at one instance; wrong the moment a second one runs. |
| 10 | `infra/` and `ocr/` are README-only. | HANDOFF states this accurately, but the README stack table reads as though they exist. |

### What should change

- **Build the Sales vertical** (deal engine → snapshots → desking) as the next slice, using Inventory
  as the template. Highest value, highest risk, most depended-on.
- **Add a regulatory engine as a first-class module**, separate from the tax rule set. Do not extend
  `sales.rule_set` to carry legal rules — the two have different lifecycles, reviewers, and shapes.
- **Add idempotency middleware before any submission endpoint ships.**
- **Add the title/lien/tag/pre-registration domain**, modelled state-neutrally, driven by rules.
- **Commission the Oklahoma source research now**, in parallel with engine work, because it gates
  go-live and cannot be compressed at the end.

### What should NOT change

- **Do not migrate to NestJS/Prisma.** See Section E, row 1. The master prompt's Step 5 defaults are
  explicitly conditional ("unless the existing system provides a compelling reason otherwise"), and
  this system provides one: a correct C# `decimal` money model, RLS-backed tenancy, SQL-first
  migrations, and architecture-test-enforced module boundaries. Prisma models RLS, triggers,
  exclusion constraints and generated columns poorly, and JavaScript has no native decimal type.
  Rewriting would trade a working correctness foundation for framework fashion at an estimated cost
  of 4–6 months.
- Do not replace PostgreSQL, the RLS tenancy model, SQL-first migrations, the `Money` type, the
  snapshot table, the audit ledger, the generated contract chain, or the modular-monolith boundaries.
- Do not introduce Kubernetes, Kafka, microservices, or a second datastore.

---

## SECTION B — Current architecture

```
                       ┌────────────────────────────────────────────┐
                       │            Browser (dealer staff)          │
                       └──────────────────────┬─────────────────────┘
                                              │ HTTPS
                       ┌──────────────────────▼─────────────────────┐
                       │  Caddy  (TLS, prod compose only)           │
                       └───────┬───────────────────────┬────────────┘
                               │                       │
              ┌────────────────▼──────────┐   ┌────────▼───────────────────────┐
              │  Next.js web (App Router) │   │  ASP.NET Core 9 API            │
              │  5 routes                 │   │  Minimal APIs · 20 endpoints   │
              │  BFF session cookie       │──▶│  JWT bearer (access + refresh) │
              │  generated api-client     │   │  Rate limiting (in-process)    │
              └───────────────────────────┘   │  ProblemDetails errors         │
                                              └───┬────────────┬───────────┬───┘
                                                  │            │           │
       ┌──────────────────────────────────────────▼──┐   ┌─────▼──────┐  ┌─▼──────────────┐
       │  Modules (Clean Architecture, 4 projects ea) │   │ Valkey     │  │ S3 / MinIO / R2│
       │  ┌──────────┐  ┌───────────┐   ┌──────────┐  │   │ (cache)    │  │ quarantine ➜   │
       │  │ Identity │  │ Inventory │   │ Sales ✗  │  │   └────────────┘  │ clean buckets  │
       │  └──────────┘  └───────────┘   └──────────┘  │                   └────────────────┘
       │  cross-module refs limited to X.Contracts    │   ┌────────────┐
       │  (enforced by ArchitectureTests)             │   │ ClamAV     │
       └──────────────────────┬───────────────────────┘   └────────────┘
                              │ EF Core, mapping TO the schema (generates no migrations)
                 ┌────────────▼─────────────────────────────────────────┐
                 │ PostgreSQL 16  ·  role: mautodesk_app                │
                 │   no BYPASSRLS · not table owner                     │
                 │   RLS ENABLE + FORCE on every tenant table           │
                 │   app.current_tenant_id() ← set_config from token    │
                 │                                                      │
                 │  12 schemas: platform app identity inventory crm     │
                 │              sales documents signatures messaging    │
                 │              publishing billing audit                │
                 │  80 tables · policies applied by generated loop      │
                 │  audit.event: append-only, hash-chained              │
                 │  sales.deal_calculation: append-only by trigger      │
                 └──────────────────────────────────────────────────────┘

  Legend:  ✗ = designed in schema/docs, no code.
  NOT BUILT: outbox dispatcher · Hangfire jobs · OCR worker · Terraform · e-sign · documents ·
             CRM UI · deals · messaging · publishing · billing · reporting
```

---

## SECTION C — Target architecture

The target is the **same architecture with three new module families and one new enforcement layer**.
It is an extension, not a replacement.

```
                        ┌───────────────────────────────────────────────┐
                        │  Next.js web — Dashboard · Customers ·        │
                        │  Inventory · Desking · Deals · Lenders ·      │
                        │  Documents · Titles · Tags · Pre-Reg ·        │
                        │  Compliance · Reports · Admin                 │
                        └───────────────────────┬───────────────────────┘
                                                │  REST /api/v1 (generated OpenAPI ➜ typed client)
        ┌───────────────────────────────────────▼─────────────────────────────────────────┐
        │                        ASP.NET Core 9  ·  modular monolith                      │
        │                                                                                 │
        │  EXISTING            NEW — deterministic engines (pure, no I/O, unit-testable)  │
        │  ┌──────────┐        ┌──────────────┐ ┌──────────────┐ ┌────────────────────┐  │
        │  │ Identity │        │ Deal Engine  │ │Lender Engine │ │ Regulatory Engine  │  │
        │  ├──────────┤        │ 25-step      │ │ eligibility  │ │ resolve + evaluate │  │
        │  │Inventory │        │ pipeline     │ │ + scoring    │ │ + audit            │  │
        │  └──────────┘        └──────┬───────┘ └──────┬───────┘ └─────────┬──────────┘  │
        │                             │                │                   │             │
        │  ┌──────────┐  ┌──────────┐ │  ┌─────────────▼──────┐  ┌─────────▼──────────┐  │
        │  │   CRM    │  │  Sales   │ └─▶│  DEAL GUARDIAN     │◀─│ Document Engine    │  │
        │  │(customer)│  │ (deals)  │    │  orchestrator      │  │ Title/Reg Engine   │  │
        │  └──────────┘  └──────────┘    │  GREEN/YELLOW/RED  │  └────────────────────┘  │
        │                                └────────────────────┘                           │
        │  Cross-cutting (new): Idempotency middleware · Outbox dispatcher · OTel traces  │
        └──────────────────────────────┬──────────────────────────────────────────────────┘
                                       │
        ┌──────────────────────────────▼──────────────────────────────────────────────────┐
        │ PostgreSQL 16 (unchanged tenancy model)                                         │
        │  + compliance schema: regulatory_jurisdiction/authority/source/source_version/   │
        │                       rule/rule_version/rule_test/override/compliance_run/result │
        │  + lending    schema: lender/program/program_version/program_rule/submission/    │
        │                       decision/stipulation                                       │
        │  + titling    schema: vehicle_title/title_hold/lien/lien_transaction/            │
        │                       temporary_tag/pre_registration/title_transaction           │
        └─────────────────────────────────────────────────────────────────────────────────┘

  Integrations behind ports (adapters only, no core contamination):
  /vin (NHTSA — built) · /credit · /lender · /title · /registration · /esign · /notifications
  Oklahoma: adapter shape UNDECIDED until the official mechanism is confirmed (Section I).
```

**Deliberately unchanged:** language, framework, ORM strategy, migration strategy, tenancy
enforcement, deployment topology, cache, object storage.

**Deliberately excluded from MVP:** Kubernetes, message broker, microservices, search engine, second
datastore, full accounting ERP.

---

## SECTION D — Gap analysis

| # | Capability (master-prompt step) | Exists today | Gap | Severity |
| --- | --- | --- | --- | --- |
| 1 | Multi-tenancy, server-side (7) | RLS forced, token-claim tenancy, CI gate | **None.** Exceeds requirement | — |
| 2 | Decimal money (9) | `Money` + `numeric(14,2)` + arch test | **None** | — |
| 3 | Calculation snapshots (13) | `sales.deal_calculation`, append-only, hashed | Table only; nothing writes to it | High |
| 4 | Deal engine (10–12) | Nothing | **Entire engine**: 25-step pipeline, amortization, gross | **Critical** |
| 5 | Lender matrix (14–15) | Nothing; `finance_terms.lender_name` is free text | **Entire domain**: lenders, programs, versions, rules, scoring, optimizer | **Critical** |
| 6 | Regulatory engine (16–19) | Tax rule sets only | **Entire engine**: rule model, DSL, resolver, evaluator, compliance runs, overrides | **Critical** |
| 7 | Oklahoma rule pack (20) | Unapproved skeleton with `'UNVERIFIED'` values | **All content**, from primary sources, with legal review | **Critical / gating** |
| 8 | Title, electronic vs paper (21–22) | Nothing (`trade_in.title_status` free text only) | Whole domain incl. hold types and VIN inspection | **Critical** |
| 9 | Pre-registration (23) | Nothing | Whole domain incl. business-day deadline as a *rule* | **Critical** |
| 10 | Temporary tags (24) | Nothing | Whole domain incl. state machine and rule-derived expiry | **Critical** |
| 11 | Advertising compliance (25) | Nothing | Separate engine | High (post-MVP candidate) |
| 12 | Document engine (26–27) | `documents.*` + `signatures.*` schema, immutable versions | Requirement engine, templates, generation, e-sign flow — all code | High |
| 13 | Deal Guardian (34) | Nothing | Orchestrator + UI | **Critical (the product)** |
| 14 | Audit (28–29) | `audit.event` hash-chained, writes wired | Compliance-run audit (`run_id`, `input_hash`, `result_hash`) missing | Medium |
| 15 | Overrides (30) | Nothing | Request → reason → approval → audit → expiry | High |
| 16 | Idempotency (45) | Table exists, **zero code references** | Middleware + key discipline on all submissions | High |
| 17 | Events / outbox (44) | `app.outbox_message` written transactionally | **No dispatcher** — nothing consumes | High |
| 18 | Integrations abstraction (39) | `IObjectStore`, VIN decoder, `IDataKeyProvider` ports exist | Retry/backoff/circuit-breaker/manual-retry not standardized | Medium |
| 19 | Observability (55) | Structured logs + PII redaction, OTel env var, health check | No traces, no metrics, no dashboards, nothing measured | Medium |
| 20 | Frontend (32–35) | 5 routes | 13 nav areas incl. desking and Guardian | High |
| 21 | Accounting (41) | `sales.commission*`, `deal_payment`; no ledger | Double-entry journal — **POSTPONE** | Low (MVP) |
| 22 | Infrastructure as code (59) | `infra/README.md` only | Terraform, or an explicit decision to stay on Compose | Medium |
| 23 | Backups / DR (56–57) | Documented targets; **never tested** | Restore rehearsal | High before pilot |
| 24 | Performance (42) | Budgets stated | Nothing profiled or load-tested | Medium |
| 25 | Rate limiting at scale | In-process partitions | Distributed (Valkey) when >1 instance | Medium |
| 26 | KMS (`RISK-SEC-001`) | Master key in secret store | Managed KMS or HSM | High before enterprise sale |

---

## SECTION E — Build / retain / refactor / replace matrix

| Component | Current | Problems | Bus. importance | Sec. risk | Effort | **Decision** | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Backend framework** | ASP.NET Core 9, C# | None material | Critical | Low | — | **RETAIN** | Native `decimal`; arch tests enforce boundaries. Migrating to NestJS discards the correctness foundation for no gain. |
| **ORM strategy** | EF Core mapping *to* SQL-first schema | None | High | Low | — | **RETAIN** | ADR-0011. Prisma cannot express RLS, triggers, exclusion constraints, generated columns. |
| **Database** | PostgreSQL 16 | None | Critical | Low | — | **RETAIN** | Matches target exactly. |
| **Tenancy** | Token claim + forced RLS + CI gap gate | None | Critical | Low | — | **RETAIN — protect** | Stronger than the prompt requires. |
| **Money model** | `Money` decimal + `numeric(14,2)` | None | Critical | Low | — | **RETAIN** | Steps 9–10 already satisfied. |
| **Calc snapshot table** | `sales.deal_calculation` | Lacks jurisdiction / regulatory-pack / lender-program provenance | Critical | Low | S | **REFACTOR (additive)** | Step 13 wants provenance beyond the tax rule set. Expand-only migration. |
| **Tax rule sets** | `sales.rule_set`, unapproved | Placeholder content only | Critical | Med | L | **RETAIN structure / BUILD content** | Structure is right. Content needs primary sources + CPA sign-off. |
| **Regulatory engine** | Absent | — | Critical | High | XL | **BUILD** | New `compliance` schema + module. Do *not* overload `sales.rule_set`. |
| **Deal engine** | Absent | — | Critical | High | XL | **BUILD** | New `Sales` module: pure engine, no I/O, exhaustively unit-tested. |
| **Lender matrix** | `finance_terms.lender_name` free text | Unmodelled | Critical | Med | L | **BUILD** | New `lending` schema; program *versions* required for reproducibility. |
| **Title / lien / tag / pre-reg** | Absent | — | Critical (OK) | Med | L | **BUILD** | New `titling` schema; state-neutral, rule-driven. |
| **Deal Guardian** | Absent | — | Critical | Low | L | **BUILD** | Orchestrator over the four engines. Owns no rules itself. |
| **Documents / e-sign** | Schema only; ADR-0009 says build | Evidence package needs counsel (`RISK-LEGAL-001`) | High | High | XL | **BUY for MVP, revisit** | A vendor carries ESIGN/UETA evidence liability. Behind `IESignatureProvider`, so building later is a swap. Reverses ADR-0009 *for MVP only*. |
| **Authentication** | Argon2id, mandatory TOTP, refresh rotation w/ reuse detection, lockout | None material | Critical | Low | — | **RETAIN** | Better than most managed tiers, with no per-MAU cost. |
| **Authorization** | 42-permission catalog, roles, sensitive flags | Deal permissions seeded but unused | High | Med | S | **RETAIN / EXTEND** | Add compliance, lender, titling permissions. |
| **API style** | Minimal APIs, generated OpenAPI, ProblemDetails | Only 20 endpoints | Critical | Low | — | **RETAIN pattern / EXTEND** | Drift gate already works. |
| **Idempotency** | Table only | No code | High | Med | S | **BUILD** | Before any submission endpoint. |
| **Outbox** | Written, not dispatched | Inert | High | Low | M | **BUILD** | Polling dispatcher + Hangfire, per ADR-0006. `TD-001` accepted. |
| **Frontend** | Next.js, 5 routes, generated client | Coverage only | Critical | Low | XL | **RETAIN stack / BUILD screens** | Stack matches target. |
| **Object storage** | R2/MinIO, quarantine-first, presigned | None | High | Low | — | **RETAIN** | Step 38 already satisfied. |
| **Cache** | Valkey, barely used | Underused | Med | Low | — | **RETAIN** | Needed for distributed rate limiting. |
| **VIN decode** | NHTSA vPIC + cache | Free source; limited option data | Med | Low | — | **RETAIN** | Add a commercial provider behind the port only if data quality demands it. |
| **Credit bureau** | Absent | — | High | **High** | M | **BUY** | Never build. Reseller agreement + FCRA obligations. Post-MVP. |
| **Vehicle history** | Absent | — | Med | Low | S | **BUY / POSTPONE** | Commodity API. |
| **Tax calculation** | Rule-set driven | Content missing | Critical | High | L | **BUILD (engine)** | Vehicle sales tax with trade credit is not what generic tax SaaS does well. |
| **OK state integration** | Absent | Mechanism unconfirmed | Critical | Med | ? | **RESEARCH FIRST** | Confirm the official mechanism with Service Oklahoma before designing an adapter. Do not scrape. Do not invent. |
| **Accounting** | Commission + payments only | No ledger | Med | Low | XL | **POSTPONE** | Export to the dealer's existing accounting system for MVP. |
| **Advertising compliance** | Absent | — | Med | Med | L | **POSTPONE to post-MVP** | Real differentiator, but not on the sell-a-car critical path. |
| **OCR worker** | README stub | Not built | Low | Med | L | **POSTPONE** | `RISK-LEGAL-002` unresolved. |
| **Email / SMS** | SMTP + Twilio config | Not wired | Med | Low | S | **BUY / RETAIN config** | Commodity. |
| **Payments** | Records, does not process | By design | Low | High | — | **POSTPONE** | Keeps PCI scope at zero. Correct for MVP. |
| **Monitoring** | Sentry DSN + OTel endpoint configured | Nothing emitting | High | Low | M | **BUILD (small)** | OTel spans + a managed backend. |
| **Infrastructure as code** | README stub | None | Med | Med | M | **BUILD when a second env exists** | Compose-on-VPS is defensible for pilot. Terraform when staging is real. |
| **Search** | PostgreSQL FTS | `TD-002` accepted | Low | Low | — | **RETAIN** | No search engine for MVP. |
| **Kubernetes / brokers / microservices** | Absent | — | — | — | — | **DO NOT ADD** | No demonstrated need. |

Effort key: S ≤ 1 wk · M 1–3 wks · L 3–8 wks · XL 8 wks+ (one experienced engineer).

---

## SECTION F — Scoring

Scored against a commercial-launch bar, not against effort spent.

| Category | Score | Justification |
| --- | --- | --- |
| Architecture | **8** | Clean Architecture, modular monolith, boundaries enforced by tests, 11 written ADRs. −2: only 2 of ~12 modules exist, so the boundaries are proven at small scale. |
| Security | **8** | Argon2id, mandatory TOTP, refresh rotation with reuse detection, exponential lockout, quarantine-first uploads, PII log redaction, gitleaks + CodeQL in CI, 11-finding review with 7 fixed. −2: `RISK-SEC-001` (no KMS), in-process rate limiter, no DAST, nothing deployed to test. |
| Database | **8** | 80 tables, 193 indexes, exclusion constraints, generated columns, immutability triggers, hash-chained audit, thoughtful partial indexes. −2: no lending/compliance/titling schemas; several tables have no consuming code. |
| API | **5** | Correct conventions and a generated, drift-gated contract — but 20 endpoints over 2 domains, and no idempotency. |
| Frontend | **3** | Correct stack, generated client, permission-shaped UI, WCAG AA tokens verified. Only 5 routes; no dashboard, deals, or desking. |
| Performance | **2** | Budgets exist; nothing profiled, load-tested, or deployed. Index strategy is reasoned but unvalidated against real plans. |
| Testing | **7** | 217 backend assertions incl. real-PostgreSQL integration and architecture tests; a genuine cross-tenant attack probe; e2e in a real browser. −3: nothing tested for the domains that carry the risk, because they do not exist. |
| DevOps | **6** | 8 gated CI jobs, migration job as a pre-deploy gate, three compose topologies, a written runbook. −4: no IaC, no staging, no deployed environment, untested restores. |
| Observability | **4** | Structured logs, correlation, redaction, health check, config present. Nothing emits traces or metrics. |
| Multi-tenancy | **9** | Best-in-class for this stage: forced RLS, unprivileged role, CI coverage gate, isolation probe, pooled-connection leak test. −1: single shared database will eventually need a noisy-neighbour answer. |
| Financial calculations | **3** | The *types* are right and enforced. No calculation exists. |
| Deal engine | **1** | Schema and ADR only. |
| Lender integration | **0** | Nothing, not even schema. |
| Document management | **2** | Schema with immutable versions and signature audit; no code. |
| Regulatory engine | **2** | Versioned, source-cited, approval-gated *tax* rule sets. No rules engine, evaluation, or compliance results. |
| Auditability | **8** | Append-only hash-chained ledger with verification, immutable snapshots, sensitive-permission flags. −2: no compliance-run audit yet. |
| Scalability | **6** | Stateless API, shared-schema RLS tenancy, outbox, object storage — the shape scales. −4: single VPS, in-process rate limiting, no dispatcher, unmeasured. |
| Maintainability | **8** | Exceptional documentation-to-code ratio, ADRs, honest known-gaps registers, gotchas captured. −2: doc volume is itself a maintenance load, and "Phases 1–10 complete" overstates product completeness to a casual reader. |
| Cost efficiency | **8** | No premature managed services; self-hosted auth avoids per-MAU fees; PostgreSQL doing outbox + jobs + search. −2: ClamAV and OCR are CPU-hungry per-tenant costs. |

**CURRENT SYSTEM SCORE: 5.2 / 10** (unweighted mean of 19 categories)

That single number misleads on its own, so it decomposes as:

- **Foundation score — 7.8 / 10** (Architecture, Security, Database, Multi-tenancy, Auditability,
  Maintainability, Testing, DevOps, Cost). *Above the bar. Build on it.*
- **Product/domain score — 2.8 / 10** (Deal engine, Lender, Regulatory, Documents, Financial calc,
  Frontend, API breadth, Performance, Observability, Scalability). *All remaining work and risk.*

**Verdict: RETAIN AND EXTEND.** A rewrite would destroy the 7.8 in order to rebuild the 2.8 — which
has to be built either way.

---

## SECTION G — Database assessment

**Retained as-is:** `platform`, `identity`, `inventory`, `crm`, `audit`, `app`, `billing`, `messaging`,
`publishing`, `documents`, `signatures`, and all of `sales` except the additive change below.

**Additive (expand-only) change to `sales.deal_calculation`** — Step 13 requires provenance the table
does not yet carry:

```
+ jurisdiction_code            text   -- 'US-OK'
+ regulatory_rule_set_version  text   -- compliance pack version, distinct from the tax rule set
+ lender_program_version_id    uuid   -- null for cash deals
```

**New schemas.** Every table gets `tenant_id` + an RLS policy, except genuinely shared reference data
(rules, sources, jurisdictions), which goes on the exemption list *with a written justification* — the
same standard `sales.rule_set` already meets.

```
compliance.regulatory_jurisdiction     lending.lender                    titling.vehicle_title
compliance.regulatory_authority        lending.lender_program            titling.title_hold
compliance.regulatory_source           lending.lender_program_version    titling.title_transaction
compliance.regulatory_source_version   lending.lender_program_rule       titling.lien
compliance.regulatory_rule             lending.lender_submission         titling.lien_transaction
compliance.regulatory_rule_version     lending.lender_decision           titling.temporary_tag
compliance.regulatory_rule_test        lending.lender_stipulation        titling.pre_registration
compliance.regulatory_override                                           titling.vin_inspection
compliance.compliance_run
compliance.compliance_result
compliance.document_requirement
```

**Non-negotiable properties for the new tables:**

- `regulatory_rule_version`, `compliance_run`, `compliance_result`, `lender_program_version`, and
  `regulatory_override` are **append-only by trigger**, matching `sales.deal_calculation`.
- `regulatory_rule_version` carries an effective-date exclusion constraint per
  `(rule_code, jurisdiction)` — the same GiST pattern `sales.rule_set` uses — so no deal date can
  ever resolve to two versions.
- A rule version without `approved_at` is invisible to the evaluator, exactly as with tax rule sets.
- `compliance_run` stores `input_hash` and `result_hash`, so "which rules applied on 2026-08-19" is
  answerable byte-for-byte.
- `temporary_tag` needs a partial unique index on `(tenant_id, tag_number)` where the status is live,
  and assignment must be idempotent.
- Indexes follow existing patterns: `(tenant_id, …)` leading, partial `where deleted_at is null`.
  No blanket indexing.

**Migration strategy: unchanged.** Forward-only `V0010__…` onwards, expand/contract, backward
compatible for one release, verified with `docker compose run --rm migrate`.

---

## SECTION H — Deal engine assessment

**Current state: nonexistent.** What follows is the build specification, not a description.

- **Pure and I/O-free.** `MautoDesk.Sales.Domain.DealEngine` takes a `DealInput` and a deserialized
  `RuleSet` and returns a `DealCalculation`. No database, no HTTP, no clock — `IClock` is injected at
  the application layer. This makes the 25-step pipeline exhaustively unit-testable without a
  container, and makes replaying a 2026 deal in 2029 trivial.
- **`decimal` throughout**, with `Money.RoundingMode` (half away from zero) named at every rounding
  call. Rounding happens at defined boundaries — per line item, then per subtotal — never implicitly.
- **The 25-step pipeline** maps to ordered, individually-traceable steps, each emitting a labelled
  intermediate so the UI can show *why* a number is what it is.
- **Amortization**: standard `P·r·(1+r)ⁿ / ((1+r)ⁿ−1)`, but behind an `IPaymentMethod` strategy,
  because simple-interest and pre-computed lenders do not agree with it. The lender *program version*
  selects the method, so a historical deal recomputes with the method that applied at the time.
- **Snapshot on every material change**, written to `sales.deal_calculation` with `engine_version`,
  both rule-set versions, the lender program version, and the content hash.
- **Refuses to price** when the governing rule set is unapproved. This must be a test, not a convention.
- **Test corpus**: every case in master-prompt Step 47, each with a hand-computed expected value
  recorded in the test — including negative equity, rounding boundaries, and over-LTV rejection.
- **Lender matrix and optimizer** live in a *separate* `Lending` module consuming the engine's output.
  The optimizer must never return a structure a BLOCKING regulatory rule rejects — enforced by running
  the regulatory evaluator over each candidate before ranking, not by hoping.

---

## SECTION I — Regulatory assessment and the Oklahoma gap

**Current state: a versioned tax/fee rule table with unapproved placeholder content, and nothing else.**

The engine to build is deterministic and data-driven:

```
Deal ─▶ Regulatory Context Builder ─▶ Rule Resolver (jurisdiction + trigger + effective date)
     ─▶ Applicable Rule Versions ─▶ Evaluator ─▶ Results (PASS / WARNING / BLOCK / REVIEW)
     ─▶ compliance_run + compliance_result + audit.event
```

- **Rule definitions are JSON in the database**, validated against a versioned JSON Schema and
  interpreted by a deterministic evaluator. **No custom programming language.** The condition
  vocabulary stays deliberately small: field path, operator, value, plus `all`/`any` grouping.
- **Rule lifecycle**: `DRAFT → REVIEW → APPROVED → SCHEDULED → ACTIVE → RETIRED`. A rule version is
  never edited in place.
- **Overrides** are request → reason → manager approval → audit event → expiring exception. There is
  no "ignore compliance" control anywhere in the UI.
- **Every rule carries provenance**: authority, source, source URL, citation, effective date, last
  verified date, source content hash.
- **AI cites; it never decides.** ADR-0004 and `app.ai_generation` already establish draft-only. The
  Guardian explanation must quote a `compliance_result` row and may never generate a rule.

### The Oklahoma gap — stated plainly

**This document asserts no Oklahoma regulatory requirement.** No source research was performed in this
pass, and the repository contains no verified Oklahoma content — its rule sets are explicitly
`'UNVERIFIED'` placeholders with `approved_at IS NULL`.

Section I of the master prompt's deliverable format — the OK rule-pack table of rule codes, sources,
effective dates, and status — **therefore cannot be filled in honestly yet**, and filling it in from
memory would be exactly the failure mode both the master prompt (Step 71) and `CLAUDE.md` prohibit.

What must happen instead, as a tracked work item on the critical path:

1. **Research from primary sources only** — Service Oklahoma, the Oklahoma Tax Commission, the
   Oklahoma Used Motor Vehicle, Dismantler and Parts Commission, the Oklahoma Motor Vehicle
   Commission, the Oklahoma Statutes (Titles 47 and 68), and the Oklahoma Administrative Code. Blogs,
   dealer forums, and third-party summaries may guide the search and may never be cited.
2. **Capture each source** with URL, retrieval date, and content hash into `regulatory_source_version`.
3. **Draft rule versions** referencing those source versions; write positive *and* negative tests.
4. **Human review** by dealer-compliance counsel and, for tax, a CPA. `approved_at` is set by a named
   reviewer, never by a developer.
5. **Only then** does a rule become ACTIVE and the engine begin returning it.

Areas requiring this treatment before go-live: dealer transaction documentation, odometer disclosure,
Buyer's Guide, We Owe, spot delivery, title processing (electronic vs paper, and the dealer
exceptions), title holds and VIN inspection, liens and releases, pre-registration and its deadline,
temporary tags, plate transfers, doc/dealer fees and any statutory cap, sales tax basis and trade-in
credit treatment, and advertising price disclosure.

**Explicitly do not** hard-code "all Oklahoma titles are electronic", any deadline in days, any fee
cap, or any tax rate. Every one of those is a rule-version field, and business-day arithmetic must use
a configured Oklahoma holiday calendar rather than a constant.

**Oklahoma state integration:** confirm the mechanism directly with Service Oklahoma's dealer support
before designing an adapter. If no supported API exists, implement a file-transfer export/import
adapter behind the same port. Do not scrape a government website. Do not invent an API.

---

## SECTION J — Security assessment

**Threat model, abbreviated to what changes with the new domains.**

| Asset | Threat | Existing control | Gap introduced by new work |
| --- | --- | --- | --- |
| Another dealer's deals | Cross-tenant read | Forced RLS + token-claim tenancy + CI gate + probe | New tables must join the same regime — the CI gate enforces it |
| Deal figures on a signed contract | Silent restatement | Append-only snapshot, hash | Extend the probe to new append-only tables |
| Compliance outcome | "Ignore compliance" | — | **New:** overrides require reason + approval + audit; no bypass path exists |
| Customer PII / SSN / DL | Disclosure | Envelope encryption + blind index; no plaintext columns; log redaction | Titling and lender submissions carry SSN — same treatment, no exceptions |
| Buy rate vs sell rate | Improper disclosure | `sales.deal.finance.read` permission, audited | Lender module must respect it |
| Submissions to state / lender | Duplicate on retry | — | **New:** idempotency keys mandatory |
| Signed documents | Mutation | Immutable document versions | E-sign evidence package (`RISK-LEGAL-001`) |
| Master encryption key | Compromise | Platform secret store | **`RISK-SEC-001` open** — managed KMS before an enterprise sale |

**Data classification** to adopt: `PUBLIC` (VIN decode, jurisdictions) · `INTERNAL` (inventory,
pricing) · `CONFIDENTIAL` (deals, gross, buy rate) · `RESTRICTED` (SSN, DL number, bank details,
credit data). RESTRICTED is envelope-encrypted with a blind index, never logged, never in a URL. The
`x-sensitive: true` OpenAPI marker and the log-redaction policy already implement the mechanism; what
is missing is the written classification and its application to the new fields.

**Priority security work, in order:** (1) idempotency middleware; (2) distributed rate limiting before
a second instance; (3) restore rehearsal; (4) managed KMS; (5) DAST against a deployed environment.

---

## SECTION K — Cost analysis

Assumes the existing topology: containers on DigitalOcean, managed PostgreSQL, Valkey, Cloudflare R2,
Cloudflare in front. Infrastructure only — no salaries, and no per-transaction vendor fees that would
be passed through to dealers.

| Dealers | App | PostgreSQL | Cache | Storage/CDN | Email/SMS | Monitoring | **Monthly total** | **Per dealer** |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 10 | 1× 4vCPU/8GB $48 | $60 (1 node) | $15 | ~$10 | ~$30 | $26 | **≈ $190–260** | ~$22 |
| 50 | 2× $48 | $120 (+replica) | $15 | ~$40 | ~$120 | $50 | **≈ $440–600** | ~$10 |
| 100 | 3× $48 | $240 (HA) | $30 | ~$80 | ~$240 | $80 | **≈ $820–1,150** | ~$10 |
| 500 | 6× $96 | $700 (HA, larger) | $90 | ~$350 | ~$1,100 | $250 | **≈ $3,200–4,500** | ~$8 |
| 1,000 | 10× $96 | $1,400 (HA + read replica) | $180 | ~$700 | ~$2,200 | $450 | **≈ $6,000–9,000** | ~$7.50 |

**Major cost drivers, in order:**

1. **PostgreSQL** — the largest and least elastic line. Shared-schema RLS tenancy is what keeps it
   from being an order of magnitude worse; database-per-tenant would break the unit economics outright.
2. **SMS** — scales with deals, not dealers. Per-message pricing dominates at 500+.
3. **Document storage and egress** — mitigated by R2's zero egress fee; that choice is worth real money.
4. **AI tokens** — capped by the existing `AI_MONTHLY_COST_CAP_USD`. Guardian explanations must be
   cached per `compliance_run` hash, not regenerated per page view.
5. **ClamAV and OCR CPU** — the reason not to co-locate them with the API at scale.
6. **Credit-bureau pulls and e-sign envelopes** — per-transaction vendor costs, best passed through.

Per-dealer infrastructure cost of roughly **$8–10/month at 100+ dealers** leaves a healthy margin
against typical DMS pricing. The architecture is not the constraint on the business model.

---

## SECTION L — MVP recommendation

**MVP = "a dealer can sell a car in Oklahoma, correctly and provably."**

In scope: existing Identity + Inventory · Customers (UI over the existing CRM schema) · Deals + deal
engine + snapshots · Desking screen · Oklahoma tax/fee rule sets (approved) · Regulatory engine +
Oklahoma pack (approved subset) · Title / lien / temp tag / pre-registration · Document requirements
and generation · E-signature via a vendor · Deal Guardian · Idempotency + outbox dispatcher · One
deployed environment with tested restores.

Out of scope for MVP, in rough order of likely return: lender submission integrations (model the
matrix, submit manually) · advertising compliance engine · double-entry accounting · OCR · marketplace
publishing · messaging · reporting beyond deal lists · Texas/Kansas packs · credit bureau · payment
processing · Terraform (until a second environment exists).

---

## SECTION M — Implementation roadmap

Dependency-ordered. The Oklahoma research track runs **in parallel from day one**, because it gates
go-live and cannot be compressed at the end.

| Phase | Work | Depends on | Effort | Acceptance |
| --- | --- | --- | --- | --- |
| **R (parallel)** | Oklahoma primary-source research; sources captured with hashes; counsel/CPA engaged | — | Continuous | Every launch rule has a cited source version and a named approver |
| **0** | *(this document)* Assessment and decisions | — | Done | Decisions accepted |
| **1** | `V0010` compliance/lending/titling schema; `deal_calculation` provenance columns; append-only triggers; RLS; isolation probe extended | 0 | M | `rls_coverage_gaps()` = 0; probe passes; migrate job green |
| **2** | Idempotency middleware wired to `app.idempotency_key`; outbox dispatcher | 1 | M | Replayed POST returns the first result; outbox drains under test |
| **3** | `Sales` module: pure deal engine, 25-step pipeline, snapshot writes | 1 | XL | Full Step-47 corpus green with hand-computed expectations; refuses unapproved rule sets |
| **4** | Regulatory engine: rule model, resolver, evaluator, compliance runs, overrides | 1 | XL | Positive + negative test per rule; run reproducible from `input_hash` |
| **5** | Oklahoma pack authored, tested, reviewed, approved | R, 4 | L | No rule ACTIVE without source version, approver, and tests |
| **6** | Lender matrix: lenders, program versions, eligibility, scoring, optimizer | 3 | L | Optimizer never returns a BLOCK-violating structure |
| **7** | Titling: title state and holds, VIN inspection, liens, temp tags, pre-registration — all rule-driven | 4, 5 | L | Deadlines and expiries come from rule versions, never constants |
| **8** | Documents: requirement engine, generation, immutable instances, e-sign vendor adapter | 4, 5 | L | Requirements derive from rules; signed instances immutable |
| **9** | **Deal Guardian**: orchestrator, GREEN/YELLOW/RED, blockers, actions | 3,4,5,6,7,8 | M | Every RED cites a `compliance_result` row |
| **10** | Frontend: dashboard, customers, desking, deals, Guardian panel, titles, tags, compliance | 3–9 | XL | Permission-shaped both ways; no business logic in components |
| **11** | Deploy: staging + production, OTel, restore rehearsal, k6 profile, distributed rate limiting | 2 | M | Restore actually performed; p95 measured against budget |
| **12** | Pilot with one dealer; hardening; KMS | 11 | M | `RISK-SEC-001` closed |
| **13+** | Advertising engine · lender submissions · accounting · Texas pack | 12 | — | Texas added with zero core changes |

**Critical path:** R → 1 → 4 → 5 → 7 → 9 → 10 → 11 → 12. Phase 3 (deal engine) runs alongside 4 and
is the largest single work item.

**Definition of Done** is the master prompt's Step 66 plus the repository's existing additions:
`rls_coverage_gaps()` at zero, the isolation probe extended to new tables, the generated contract
regenerated, and — for regulatory features — source, effective date, rule version, positive and
negative tests, and human approval.

---

## Immediate next step

The highest-value work unblocking the most downstream phases is **Phase 1: the `V0010` migration**
introducing the `compliance`, `lending`, and `titling` schemas with their append-only triggers, RLS
policies, and effective-date exclusion constraints — because the deal engine, regulatory engine,
lender matrix, and Guardian all bind to it, and it is the one artefact that is expensive to change
later.
