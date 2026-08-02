# MautoDesk — working agreements

Read `docs/00-constitution.md` first; it governs everything. `docs/02-architecture.md` §2 holds the
ADRs and is the answer to most "why is it done this way" questions.

## Current phase

Phases 1–4 are complete. The schema is applied and verified; there is no application code yet.
Phase 5 is UI/UX, Phase 6 is the backend.

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
- Every operation in `contracts/openapi.yaml` carries `x-permission`. Sensitive fields carry
  `x-sensitive: true`, which the log-redaction policy consumes.

## What not to do

- Do not add plaintext columns for SSN, driver's licence number, or bank details. They are
  envelope-encrypted with a blind index for search.
- Do not put tax rates, fee amounts, or statutory caps in code or in a seed file from memory. They
  are versioned, effective-dated, source-cited rows in `sales.rule_set`, and the engine ignores any
  row without `approved_at`.
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
npx --yes @redocly/cli@latest lint contracts/openapi.yaml --config contracts/redocly.yaml
```
