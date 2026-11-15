# MautoDesk — working agreements

Read `docs/00-constitution.md` first; it governs everything. `docs/02-architecture.md` §2 holds the
ADRs and is the answer to most "why is it done this way" questions.

## Current phase

Phases 0–4 of the roadmap in `docs/11-dealeros-assessment.md` §M are complete: schema and RLS, the
pure deal engine, and the regulatory engine. Phase 2 is partial — idempotency middleware and the
outbox dispatcher exist, but no endpoint requires a key and `LoggingEventPublisher` delivers to
nothing. Auth, inventory and the deal/compliance endpoints are implemented; customers, leads,
documents, signatures, uploads, jobs and reports are designed in the contract and not built.

**The system computes no deal today, by design.** Every seeded rule set is unapproved, so the deal
engine refuses and the regulatory engine reports GREEN meaning "nothing is approved" rather than
"this deal is lawful". Note the second meaning GREEN no longer has: as of `RuleEvaluator` 2.0.0 a
rule that cannot be decided because a fact was never captured returns `UNKNOWN` and the run is
`GRAY`, so GREEN means "every applicable rule was decided on complete facts". See `docs/18`. Phase 5 — counsel review and approval of the Oklahoma pack — is the gate,
and it blocks phases 7, 9 and 10. The review packet is `docs/13-oklahoma-counsel-review.md`; the
first review came back conditional with nothing approved — see
`docs/14-oklahoma-review-disposition-2026-11-13.md`, which also records that HB 3982 and HB 3148
(both effective 2026-11-01) post-dated the drafted rules; `V0013` drafts against both.

**The gate is now waiting on people, not machinery.** `V0014` adds the
`mautodesk_rulekeeper` role and the `rulekeeper` CLI, which is the only thing that can record an
approval — as a **new version row**, never an edit, because the table is append-only. Read
`docs/17-approval-path.md` before touching anything in that area. `docs/15` scopes the counsel
engagement; `docs/16` is the tax field matrix, and `V0015` archives the first two tax statutes
without putting a number in any rule set.

Not built: lender matrix and titling (schema only, no C#), documents and e-signature, Deal
Guardian, most of the frontend, deployment and observability. `RISK-SEC-001` (no managed KMS) is
open.

## Hard rules

These are enforced by CI. Breaking one produces a red build, and that is deliberate.

- **Tenancy is resolved from the access-token claim only.** Never a header, subdomain, query
  parameter, or request body. There is no `X-Tenant-Id` in this system.
- **Every new tenant-owned table needs `tenant_id` and an RLS policy.** `app.rls_coverage_gaps()`
  must return zero rows. Do not resolve a gap by adding the table to `app.rls_exempt_table` without a
  reviewed justification — that list is for genuinely shared reference data only.
- **Money is `decimal` in C#, `numeric(14,2)` in PostgreSQL, and a *string* in JSON.** No `float`,
  no `double`, no JSON numbers for amounts.
- **`sales.deal_calculation`, `audit.event`, `documents.document_version`, and
  `signatures.audit_entry` are append-only.** Triggers block mutation. A correction is a new row.
- **AI output is a draft.** It never reaches a consumer without explicit human approval, and it is
  grounded only in decoded or dealer-entered data.
- **An absent fact is never a pass.** Conditions are three-valued. A rule that cannot be decided
  produces `UNKNOWN` and names the missing facts; it must not be collapsed into "did not fire".
  `ComplianceStatus` and `DealReadiness` are pinned to their database check constraints by an
  architecture test, so widening either means changing both.
- **Uploads go to quarantine first.** Declared content type, size, and hash are verified against the
  actual object, never trusted.

## Schema changes

`db/migrations/*.sql` is the single source of truth (ADR-0011). EF Core maps *to* the schema and
generates no migrations.

- Add a new forward-only file: `V####__description.sql`. Never edit an applied migration.
- Migrations are expand/contract and must stay backward-compatible with the deployed application for
  one release. Destructive changes are a separate, later migration.
- After any schema change, run the verification the same way CI does:

```bash
docker compose run --rm migrate
```

## Conventions

- SQL: `snake_case`; indexes `_ix`, unique `_uq`, checks `_ck`, foreign keys `_fk`. Enum-like columns
  are `text` + `check`, not PostgreSQL enum types.
- JSON: `camelCase`. Paths: lowercase plural nouns. Non-CRUD actions are a `POST` to a verb
  sub-path (`/deals/{id}/calculate`).
- Timestamps are `timestamptz` and UTC everywhere. There is no local-time column in this system.
- There are two contracts. `contracts/openapi.design.yaml` is the hand-written Phase-4 design and
  still describes endpoints nobody has built. `contracts/openapi.json` is generated from the
  running API and is what the TypeScript client comes from; `ContractGenerationTests` fails on
  drift (ADR-0010). Regenerate it rather than editing it.
- Every operation in `contracts/openapi.design.yaml` carries `x-permission`. Sensitive fields carry
  `x-sensitive: true`, which the log-redaction policy consumes.

## What not to do

- Do not add plaintext columns for SSN, driver's licence number, or bank details. They are
  envelope-encrypted with a blind index for search.
- Do not put tax rates, fee amounts, or statutory caps in code or in a seed file from memory. They
  are versioned, effective-dated, source-cited rows in `sales.rule_set`, and the engine ignores any
  row without `approved_at`. `sales.rule_set` is append-only as of `V0017`: an approval is a new
  version row written by `taxkeeper`, never an edit.
- Do not resolve a `compliance.pending_approval` item in a migration, a seed, or a test. The
  register is the saved place for a human's disposition; a migration that filled one in would be
  approving its own work. CI asserts the register ships fully open.
- Do not denormalize deal totals onto `sales.deal`. The calculation snapshot is the only source of
  truth for a number that can appear on a contract.
- Do not use `DateTime.Now`/`UtcNow` outside the `IClock` implementation — an architecture test
  fails on it.
- Do not reference another module's `Domain`, `Application`, or `Infrastructure` project. Only
  `X.Contracts` is public.

## Useful commands

```bash
docker compose up -d postgres valkey minio minio-init mailpit
```

```bash
docker compose run --rm migrate
```

```bash
npx --yes @redocly/cli@latest lint contracts/openapi.design.yaml --config contracts/redocly.yaml
```
