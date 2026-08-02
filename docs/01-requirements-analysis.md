# MautoDesk — Phase 1: Requirements Analysis

**Status:** Draft for review · **Phase:** 1 of 13 · **Owner:** Architecture
**Preceded by:** `docs/00-constitution.md` (master spec) · **Followed by:** Phase 2 — Architecture

---

## 1. Product definition

MautoDesk is a multi-tenant SaaS Dealer Management System (DMS) for **independent and small
used-car dealerships** (roughly 1–25 employees, 20–500 units of inventory). It competes with
AutoManager / DealerCenter / Frazer on capability, and beats them on UX, speed, and AI automation.

**Primary value proposition:** enter data once. A VIN typed at acquisition should flow untouched
through merchandising, the website, the lead, the deal, the contract, the signature, accounting,
and the report — with AI filling the gaps a human would otherwise type.

### 1.1 What this product is not (explicit non-goals for v1)

| Excluded | Rationale |
| --- | --- |
| Franchise/OEM dealer support (DMS certification, OEM feeds, warranty claims) | Certification programs (CDK/Reynolds-class) are a multi-year, contract-gated effort |
| Buy-Here-Pay-Here loan servicing / in-house note portfolio | Regulated lending servicing; large distinct domain — designed for, not built in v1 |
| Full general ledger accounting | We integrate with QuickBooks/Xero rather than become a ledger of record |
| Electronic title & registration (ETR/EVR) filing | State-by-state DMV vendor certification; adapter seam only in v1 |
| Native mobile apps | Responsive PWA covers the lot-walk use case at a fraction of the cost |

---

## 2. Personas and their jobs

| Persona | Daily reality | What they need | Failure mode if we get it wrong |
| --- | --- | --- | --- |
| **Dealer Principal / Owner** | Wears every hat; checks the business on a phone at night | Dashboard: what sold, what's aging, what's the gross, what's stuck | Reports that don't tie out → loses trust in the whole system |
| **Sales / F&I person** | On the lot, phone in hand, customer standing there | Fast VIN scan, photo upload, quote → buyer order in minutes | Anything over 3 taps or a slow page → they use paper instead |
| **Office Manager / Title clerk** | Paperwork, titles, DMV packets, deal jackets | Document completeness, version history, "what's missing on this deal" | A missing document discovered at title time costs real money |
| **Recon / Lot tech** | Moving cars, taking photos | Scan VIN → attach costs/photos, mark stage | Requires a desktop → recon costs never get captured |
| **Service writer** (Phase 2 module) | RO intake, parts, labor | Appointments, ROs, tie to inventory | — |
| **Platform admin (us)** | Tenant provisioning, support, billing | Tenant management, impersonation with audit, usage metering | Support impersonation without an audit trail = compliance finding |

**Design consequence:** the mobile experience is not a downgrade of the desktop app. Recon, photo
capture, VIN scan, and lead response are **mobile-first**; deals, accounting, and reporting are
desktop-first.

---

## 3. Tenancy model

**Decision: shared database, shared schema, `TenantId` on every tenant-owned row, enforced by
PostgreSQL Row-Level Security (RLS) — not only by application query filters.**

Rationale:

- Application-level filtering (EF Core global query filters) is one forgotten `IgnoreQueryFilters()`
  away from a cross-tenant data breach. RLS makes the database the last line of defense, so a bug in
  the data layer produces zero rows instead of another dealership's customers.
- Shared schema keeps migrations, connection pooling, and cost linear for hundreds of small tenants.
  Schema-per-tenant costs are dominated by migration fan-out at exactly our tenant size.
- Escape hatch preserved: a large or contractually isolated tenant can be moved to a dedicated
  database later because the tenant boundary is already explicit in every query path.

Isolation must be re-established, not assumed, at every layer:

| Layer | Mechanism |
| --- | --- |
| Database | RLS policies keyed to a per-request session variable set from the authenticated principal |
| API | Tenant resolved from the access token claim — **never** from a request header, body, or query param |
| Cache (Redis) | Tenant ID is a mandatory key prefix; a cache helper that cannot build an untenanted key |
| Object storage | Key prefix `tenant/{tenantId}/...` + per-object access policy; all access via short-lived signed URLs |
| Background jobs | Tenant context serialized into the job payload and re-established (including the DB session var) at execution |
| Search / reports / exports | Same predicate path as online queries; no separate "reporting" connection that bypasses RLS |

**Cross-tenant is not a feature.** Platform-admin access is a distinct principal type with its own
role, its own audit stream, and mandatory reason-for-access capture.

---

## 4. Functional scope and release sequencing

Modules ranked by whether a dealership can operate without them. "MVP" = the smallest set that
replaces a real dealer's current tool, not the smallest demoable set.

### Release 1 — MVP (the sellable core)

1. **Identity & tenancy** — signup, tenant provisioning, users, roles, MFA, sessions, audit log
2. **Inventory** — VIN decode, vehicle record, costs, recon, status, aging, photos, notes, timeline
3. **Photo pipeline** — bulk upload, resize/optimize, ordering, captions, CDN delivery, watermark
4. **CRM core** — customers, leads, tasks, notes, activity timeline, lead source
5. **Deals** — quote → buyer order, trade-in, deposits, fees/taxes, gross profit
6. **Documents** — upload, generate from template, version history, deal jacket
7. **E-signature** — in-person and remote signing with a defensible ESIGN/UETA audit trail
8. **Reporting core** — inventory aging, gross profit, sales by salesperson, deal pipeline
9. **Billing** — subscription, plan limits, metering

### Release 2 — Differentiation

10. **AI service layer** — descriptions, pricing suggestions, lead summaries, reply drafting
11. **OCR pipeline** — driver license, title, registration, insurance
12. **Messaging** — email + SMS, threaded per customer, templates
13. **Website / marketplace publishing** — feed generation and syndication
14. **Appointments & calendar**

### Release 3 — Expansion

15. Service department / repair orders · Accounting integration (QuickBooks/Xero) · Advanced
    analytics · Public API for tenants · Commission plans · Multi-location/rooftop support

**Sequencing rule:** no module ships without its slice of RBAC, audit logging, tests, and API
documentation. "We'll add permissions later" is how the security review in Phase 9 fails.

---

## 5. Non-functional requirements (measurable, not aspirational)

| Category | Requirement | How it is verified |
| --- | --- | --- |
| Latency | p95 < 200 ms, p99 < 500 ms for read APIs at 500 vehicles / 50k customers per tenant | k6 load profile in CI against seeded data |
| Latency | p95 < 800 ms for write/deal-calculation endpoints | Same |
| Frontend | LCP < 2.0 s, INP < 200 ms on 4G / mid-tier Android | Lighthouse CI budget, fails the build |
| Throughput | 1,000 tenants × 10 concurrent users on a horizontally scalable tier | Load test before GA |
| Availability | 99.9 % monthly for the API; degraded-but-readable if AI/OCR providers are down | Synthetic monitoring + error budget |
| Durability | RPO ≤ 15 min, RTO ≤ 4 h; PITR enabled; **restores rehearsed quarterly** | Documented restore drill |
| Scale ceiling (design) | 50k vehicles and 5M documents per tenant without schema change | Index and partitioning plan in Phase 3 |
| Accessibility | WCAG 2.2 AA | axe in CI + manual keyboard pass per module |
| Browser support | Last 2 versions of Chrome/Edge/Safari/Firefox; iOS Safari 16+ | Playwright matrix |
| Localization | en-US v1; all user-facing strings externalized, `Money` and dates never formatted ad hoc | Lint rule against hardcoded strings |
| Data correctness | All monetary math in `decimal`/integer minor units — **never** floating point | Static analysis + unit tests |

**Note on the sub-200 ms target:** it is achievable for tenant-scoped reads with correct indexing and
Redis caching. It is *not* achievable for VIN decode, AI generation, OCR, or marketplace sync — those
are inherently third-party or model-bound. Those operations are **asynchronous by design** (job +
status polling / push), so the *interactive* API stays inside budget. This is an architectural
commitment, not a caveat.

---

## 6. Security requirements — grouped by what they defend against

The constitution lists ~45 controls. They collapse into eight defended properties. Phase 2 maps each
to a specific component; Phase 9 audits each with a test.

| Property | Controls |
| --- | --- |
| **Only the right people get in** | Argon2id password hashing, MFA (TOTP + WebAuthn-ready), OAuth/OIDC SSO, account lockout with backoff, credential-stuffing protection, session expiry, refresh-token rotation with reuse detection |
| **They only see their own tenant** | RLS, token-derived tenant claim, per-layer isolation (§3), object-storage prefix policies |
| **They only do what their role allows** | RBAC with fine-grained permissions, least privilege by default, deny-by-default authorization, permission checks in the application service layer (not the controller) |
| **Data is unreadable if stolen** | TLS 1.2+ everywhere, encryption at rest (DB, blobs, backups), envelope encryption for PII fields (SSN, DL number, bank details) via a managed KMS, secrets in a vault — never in config files or env-var sprawl |
| **The app can't be turned against the user** | CSP with nonces, output encoding, strict CORS allowlist, CSRF protection, secure/HttpOnly/SameSite cookies, parameterized queries only, full input validation via shared Zod/FluentValidation contracts |
| **Uploads can't hurt us** | Content-type *and* magic-byte verification, extension allowlist, size caps, image re-encoding to strip payloads/EXIF-GPS, antivirus scan seam, quarantine bucket until clean, never serve user content from the app origin |
| **Abuse is bounded** | Per-tenant + per-IP + per-endpoint rate limiting, request throttling, idempotency keys on mutating endpoints, cost caps on AI/OCR per tenant |
| **We can prove what happened** | Append-only audit events (actor, tenant, action, entity, before/after, IP, UA, correlation ID), hash-chained for tamper evidence, structured logs with automatic PII redaction, distributed tracing |

**PII inventory (drives encryption, retention, and DSAR handling):** name, address, phone, email,
date of birth, **SSN**, **driver's license number and image**, employment and income, **bank account
and routing numbers**, credit application data, and credit score/bureau responses. This is a
high-value target. It is why FTC Safeguards applies (§7) and why field-level encryption for the
bolded items is a v1 requirement, not a v2 nicety.

---

## 7. Compliance obligations

| Regime | Applies because | Concrete v1 requirement |
| --- | --- | --- |
| **FTC Safeguards Rule** | Dealers are "financial institutions"; we are their **service provider** handling customer financial information | Written infosec program, designated qualified individual, risk assessment, encryption in transit + at rest, **MFA mandatory** for all users with access to customer info, access controls, activity monitoring, secure disposal, incident response plan, annual penetration testing + biannual vulnerability assessment, vendor oversight. Dealers will ask us to sign this in their vendor questionnaire — build to it from day one, it is not retrofittable. |
| **ESIGN / UETA** | We produce legally binding retail contracts | Explicit consumer consent to electronic records (captured, timestamped, revocable), disclosure of hardware/software requirements, signer intent, attribution evidence, association of signature with the record, and the ability of the consumer to **retain a copy**. Audit trail: signer identity, auth method, IP, user agent, timestamps (UTC), document hash before and after signing. |
| **GDPR / CCPA readiness** | Not required for a US-only used-car dealer today; required the moment we grow | Data map, purpose/consent tracking, per-subject export and deletion capability (soft delete ≠ deletion — need a real erasure path), processor terms, retention policies |
| **Gramm-Leach-Bliley privacy notice** | Rides along with Safeguards | Store and version the dealer's privacy notice + delivery evidence |
| **Records retention** | State DMV rules; commonly 4–7 years for deal jackets | Per-tenant retention policy engine; legal hold that blocks purge |
| **Driver's license scanning** | Several states restrict capturing/retaining DL data and its permitted uses (and biometric laws like BIPA can attach to face images) | Purpose limitation, configurable retention, no secondary use, per-tenant opt-in — **flagged for legal review before the OCR module ships** |

> ⚠️ **Not legal advice.** Compliance postures above are engineering requirements derived from a
> reading of the rules; a licensed attorney must review before GA, especially §7 rows 1, 2, and 6.

---

## 8. Integrations — with honest availability assessment

The constitution's integration list mixes "free and open" with "contract-gated." That distinction
drives the architecture: **every external system sits behind our own adapter interface with a
recorded contract, so a provider swap is a configuration change, not a refactor.**

| Integration | Reality | v1 plan |
| --- | --- | --- |
| **NHTSA vPIC VIN decode** | Free, public, no key. Good for year/make/model/engine/body. **Does not give trim-level or factory options** reliably | Ship on vPIC; `IVinDecoder` abstraction so a paid decoder (DataOne, Chrome, Experian) drops in when trim accuracy matters for pricing |
| **Book values** (J.D. Power, Black Book, KBB) | Paid, contract + per-lookup fees, no self-serve signup | Adapter interface + manual-entry fallback in v1. Do not block MVP on a signed contract |
| **Vehicle history** (Carfax, AutoCheck) | Paid dealer subscription; display terms are contractual | Adapter + "link out with dealer's own account" fallback |
| **Cars.com / AutoTrader / CarGurus** | Ingest listings via **dealer-authorized data feeds** (XML/CSV over FTP/SFTP or HTTP), not open write APIs. The dealer must authorize us as their feed provider | Build a **feed generation + delivery engine** (this is the actual product surface), plus per-destination field mapping. Treat "API integration" as the exception, not the rule |
| **Facebook Marketplace** | **No general public listing API for vehicles.** Legitimate paths are Meta's partner/catalog programs or dealer-side manual posting. Scraping/automation violates ToS and risks the dealer's account | Generate a compliant export + assisted posting flow in v1; pursue partner status separately. **Do not promise automated FB posting in marketing copy.** |
| **Dealer website** | Ours to define | First-class: public JSON feed + hosted inventory pages + embeddable widget |
| **Email** | Commodity (SES/SendGrid/Postmark) | Adapter; DKIM/SPF/DMARC per tenant sending domain |
| **SMS** | Commodity (Twilio) but **A2P 10DLC brand/campaign registration is mandatory** in the US and takes days-to-weeks per tenant | Build tenant onboarding for 10DLC into the messaging module — this is a real launch-blocking lead time, not a checkbox |
| **Payments** | Stripe for **our** SaaS subscription billing; dealer-facing customer payments (deposits) are a separate, higher-risk integration | v1: subscription billing only. Deposits recorded, not processed |
| **Accounting** | QuickBooks Online API / Xero API — solid, OAuth2, self-serve dev accounts | Release 3; design the deal→journal-entry mapping in Phase 3 so it isn't retrofitted |
| **Auction inventory** | Varies wildly by source; many are contract-gated or unofficial | Deferred; adapter seam only |
| **Calendar** | Google/Microsoft Graph, OAuth2 | Release 2 |

**Architectural consequence:** the "Integrations" module is not a folder of API clients. It is a
**provider registry** — typed adapter interfaces, per-tenant credential vaulting, circuit breakers,
retry with backoff and DLQ, response caching, per-provider cost metering, and a sync-status UI that
tells the dealer exactly what published where and when it last failed.

---

## 9. Cross-cutting technical decisions requiring resolution in Phase 2

| # | Decision | Options | Recommendation |
| --- | --- | --- | --- |
| D-1 | Backend runtime | ASP.NET Core (constitution's preference) vs NestJS | **See §11 — blocking, user's call** |
| D-2 | Cloud provider | Azure vs AWS | Pick one now; the storage/KMS/queue/identity choices cascade immediately |
| D-3 | OCR service language | PaddleOCR + OpenCV are Python. A .NET backend means a **separate Python worker service** (extra deploy target, extra CI, extra security surface) | Accept the polyglot worker, isolated behind a queue + HTTP contract. Do not attempt to port OCR to .NET |
| D-4 | AI provider | Claude API via a provider-agnostic `IAiCompletionService` | Abstract from day one: prompts, model IDs, token accounting, and per-tenant cost caps live in our layer, not scattered in features |
| D-5 | Event backbone | In-process (MediatR/Nest events) vs durable broker | Start with a durable job queue + outbox pattern. Avoid a full event bus until a second service actually needs it |
| D-6 | E-signature | Build (per constitution) vs DocuSign/Dropbox Sign | Build, but **only** after Phase 2 specifies the exact ESIGN evidence package. This is the single highest legal-risk component we own |
| D-7 | Monorepo layout | Single repo with `apps/` + `packages/` vs split repos | Monorepo. Shared contracts (OpenAPI → generated TS client) are the whole point |
| D-8 | Deal math engine | Ad hoc vs a versioned, pure, fully-tested calculation module with immutable snapshots per deal version | Versioned engine. Tax/fee rules change; a deal signed in March must recalculate identically in an audit two years later |

---

## 10. Top risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Scope: the constitution describes ~5 products (DMS + CRM + DXP + OCR + e-sign) | Never ships | Enforce the Release 1/2/3 gates in §4. Ship a dealer-usable MVP before Release 2 starts |
| Deal math / tax / fee correctness varies by state and county | Wrong contracts = legal and financial exposure | D-8 versioned engine, per-jurisdiction rule data, golden-file tests against real deal examples per state we launch in |
| E-signature legal defensibility | A contested contract fails in court | Formal evidence-package spec, PDF/A + hash chain, third-party legal review, timestamping authority |
| Marketplace syndication is contract- and ToS-gated, not technical | Promised feature can't be delivered | Feed engine + honest UI language; pursue partnerships in parallel; never automate against ToS |
| Cross-tenant leak | Existential for a SaaS | RLS as the backstop (§3) + an automated test suite that runs every endpoint as tenant B against tenant A's IDs and asserts 404 |
| AI hallucination in vehicle descriptions or pricing | Consumer-protection exposure (advertising a feature the car lacks) | AI outputs are **drafts requiring human approval**, grounded strictly in decoded/entered data, never free-invented; log prompt + output for every generation |
| Unbounded AI/OCR cost per tenant | Margin destruction | Per-tenant quotas and hard caps metered in the AI service layer from the first commit |
| Data migration from incumbent DMS | Dealers won't switch without their history | Importer with a documented CSV/mapping spec is a **Release 1 sales requirement**, not a nice-to-have |
| Small team + 13 phases | Burnout / half-finished modules | Vertical slices: every module ships end-to-end (DB → API → UI → tests → docs) before the next starts |

---

## 11. Open questions blocking Phase 2

1. **Backend runtime (D-1).** The constitution prefers ASP.NET Core; NestJS is the stated
   alternative. **No .NET SDK is installed on this machine** (Node 24, pnpm, Docker, git are). This
   is the single decision that shapes every subsequent artifact.
2. **Cloud provider (D-2).** Azure or AWS. Determines Blob vs S3, Key Vault vs KMS, Service Bus vs
   SQS, and the IaC toolchain.
3. **Launch geography.** Which state(s) first? Tax, fee, title, and DL-scanning rules are
   state-specific and drive the deal engine's first rule set.
4. **First vertical slice.** Recommendation: **Inventory + VIN decode + photos**, end to end
   including auth and tenancy — it exercises every layer (DB, RLS, API, storage, background jobs,
   frontend, tests) and produces something a dealer can see on day one.

---

## Appendix A — Requirement traceability

Every requirement above carries an ID for traceability into Phase 2 (architecture), Phase 3
(schema), Phase 4 (API contracts), and Phase 10 (tests). IDs are assigned when this document is
approved, so that review-driven changes don't churn the numbering.
