# MautoDesk

A multi-tenant SaaS dealership management system for independent and small used-car dealerships.

**Current state: Phases 1–10 complete. 234 assertions across 6 suites, zero known dependency vulnerabilities.**

---

## What exists today

| Area | Status |
| --- | --- |
| Requirements analysis | ✅ [`docs/01-requirements-analysis.md`](docs/01-requirements-analysis.md) |
| System architecture + 11 ADRs | ✅ [`docs/02-architecture.md`](docs/02-architecture.md) |
| Database schema | ✅ 68 tables, 193 indexes, 65 RLS policies — **applied and verified against PostgreSQL 16** |
| Tenant isolation (database) | ✅ 13/13 checks in [`db/tests/isolation_probe.sql`](db/tests/isolation_probe.sql) |
| API contract | ✅ **Generated** from the endpoints into [`contracts/openapi.json`](contracts/openapi.json); drift fails the build. [`openapi.design.yaml`](contracts/openapi.design.yaml) remains the Phase 4 design spec |
| UI/UX + design system | ✅ [`docs/05-ux-design.md`](docs/05-ux-design.md) · **74/74 contrast pairs pass WCAG 2.2 AA** in both themes |
| Backend — Inventory slice | ✅ [`docs/06-backend.md`](docs/06-backend.md) · **56/56 tests green** (32 unit, 8 architecture, 16 integration) |
| Tenant isolation (full stack) | ✅ 16/16 integration tests against real PostgreSQL |
| Authentication | ✅ [`docs/08-authentication.md`](docs/08-authentication.md) · Argon2id, mandatory TOTP MFA, refresh rotation with reuse detection, exponential lockout |
| Security review | ✅ [`docs/09-security-review.md`](docs/09-security-review.md) · 11 findings, 7 fixed; rate limiting, endpoint enumeration and authorization matrix added |
| Testing | ✅ [`docs/10-testing.md`](docs/10-testing.md) · 63 unit, 8 architecture, 58 integration, 21 frontend, 74 token pairs, **11 end-to-end in a real browser** |
| Frontend — Inventory screens | ✅ [`docs/07-frontend.md`](docs/07-frontend.md) · grid + detail rendering live data; permission-shaped UI verified both ways |

---

## Stack

| Layer | Choice |
| --- | --- |
| Backend | ASP.NET Core 9, Clean Architecture, modular monolith |
| Frontend | Next.js (App Router), React, TypeScript, Tailwind, TanStack Query/Table, Zod |
| Database | PostgreSQL 16 with row-level security |
| Cache | Valkey (DigitalOcean Managed Redis) |
| Object storage | Cloudflare R2 (S3 API) |
| Background work | Transactional outbox + Hangfire on PostgreSQL |
| OCR | Python worker: OpenCV + PaddleOCR, behind a queue |
| Hosting | DigitalOcean App Platform → DOKS; Cloudflare for DNS, WAF, CDN |

Rationale for each choice is in [`docs/02-architecture.md`](docs/02-architecture.md) §2.

---

## Getting started

**Prerequisites:** Docker, Node 24 + pnpm, and the .NET 9 SDK (`winget install Microsoft.DotNet.SDK.9`).

```bash
cp .env.example .env
```

Bring up the dependencies and apply the schema:

```bash
docker compose up -d postgres valkey minio minio-init mailpit
```

Then run migrations and verify isolation in one step:

```bash
docker compose run --rm migrate
```

That command applies every migration, asserts `app.rls_coverage_gaps()` returns zero rows, and runs
the cross-tenant isolation probe. It exits non-zero if any check fails — a broken schema surfaces
here, not in review.

| Service | Where |
| --- | --- |
| PostgreSQL | `localhost:5432` (`postgres` / `devpw`) |
| Valkey | `localhost:6379` |
| MinIO console | http://localhost:9001 (`mautodesk` / `devpassword`) |
| Mailpit | http://localhost:8025 |

---

## Repository layout

```
docs/            Phase documents 00–07. Read 00 (the constitution) first.
db/
  migrations/    SQL migrations — the SINGLE SOURCE OF TRUTH for the schema (ADR-0011)
  tests/         isolation_probe.sql — runs as the app role and tries to break tenancy
contracts/       openapi.json (GENERATED) + openapi.design.yaml (Phase 4 spec)
backend/         ASP.NET Core solution                      (Phase 6)
frontend/        apps/web + packages/api-client (generated) + packages/ui
ocr/             Python OCR worker                          (Release 2)
infra/           Terraform for DigitalOcean and Cloudflare  (Phase 12)
.github/         CI
```

---

## Non-negotiables

These are enforced by tests, not by convention. Changing one is an architecture decision, not a
refactor.

1. **Tenancy comes from the signed token claim.** Never from a header, subdomain, query parameter, or body. The only cross-tenant reads are two `SECURITY DEFINER` functions returning ids and nothing else.
2. **The database is the last line of defence.** The app connects as `mautodesk_app`, which has no
   `BYPASSRLS` and is not the table owner. RLS is enabled *and forced* on every tenant-owned table.
3. **A new tenant-owned table without an RLS policy fails CI.** `app.rls_coverage_gaps()` must return
   zero rows.
4. **Money is `decimal` / `numeric(14,2)`.** No `float`, no `double`, anywhere — including in JSON,
   where amounts are strings.
5. **Deal figures are immutable snapshots.** `sales.deal_calculation` blocks `UPDATE` and `DELETE` by
   trigger. A correction is a new version.
6. **AI output is a draft.** Nothing model-generated reaches a consumer without explicit human
   approval.
7. **Uploads are guilty until proven clean.** Quarantine bucket → magic-byte check → hash check →
   virus scan → re-encode → promote. User content is never served from the app origin.
8. **The audit ledger is append-only and hash-chained.** `audit.verify_chain()` detects tampering
   that bypassed the application.

---

## Known open items

Carried deliberately, tracked in [`docs/02-architecture.md`](docs/02-architecture.md) §12.

| ID | Item |
| --- | --- |
| `RISK-SEC-001` | DigitalOcean has no managed KMS. Envelope encryption uses a master key in the platform secret store — weaker than an HSM. Close before the first customer security review. |
| `RISK-LEGAL-001` | The e-signature evidence package needs attorney review and RFC-3161 timestamping before GA. |
| `RISK-LEGAL-002` | Driver's-licence scanning and retention rules vary by state. Review before the OCR module ships. |
| **Tax rule sets** | The OK/KS/TX rule sets are **unapproved skeletons with placeholder values**. The deal engine refuses to price a deal until they are populated from cited primary sources and signed off. This is intentional. |

---

## Documentation

| Document | Contents |
| --- | --- |
| [`docs/00-constitution.md`](docs/00-constitution.md) | The governing specification |
| [`docs/01-requirements-analysis.md`](docs/01-requirements-analysis.md) | Scope, personas, NFRs, compliance, integration reality check |
| [`docs/02-architecture.md`](docs/02-architecture.md) | System design and ADRs 0001–0011 |
| [`docs/03-database-design.md`](docs/03-database-design.md) | Schema rationale, isolation, index strategy, retention |
| [`docs/04-api-contracts.md`](docs/04-api-contracts.md) | API conventions: errors, paging, idempotency, rate limits |
| [`docs/05-ux-design.md`](docs/05-ux-design.md) | Colour doctrine, typography, flows, states, accessibility |
| [`docs/06-backend.md`](docs/06-backend.md) | Backend structure, decisions, and known gaps |
| [`docs/07-frontend.md`](docs/07-frontend.md) | The contract chain, frontend decisions, verification |
| [`docs/08-authentication.md`](docs/08-authentication.md) | Auth decisions, the tenant boundary at login, attack-path tests |
| [`docs/09-security-review.md`](docs/09-security-review.md) | Control coverage with evidence, findings, FTC Safeguards posture |
| [`docs/10-testing.md`](docs/10-testing.md) | Test strategy, what each layer buys, and what is still untested |
