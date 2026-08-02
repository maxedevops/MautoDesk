# Backend — ASP.NET Core 9

**Status: not yet implemented.** This directory holds the build configuration and the structure
Phase 6 will fill in. `Directory.Build.props` is real and applies to every project created here.

Requires the .NET 9 SDK, which is **not currently installed on this machine**:

```bash
winget install Microsoft.DotNet.SDK.9
```

## Intended layout (ADR-0001)

```
MautoDesk.sln
src/
  MautoDesk.Api/            Composition root only: endpoints, DI, middleware, auth
  MautoDesk.PublicApi/      Website feed + syndication. Anonymous-safe, no PII, separate limits
  MautoDesk.Worker/         Hangfire host, outbox dispatcher, job handlers
  MautoDesk.SharedKernel/   Entity, Result<T>, Money, DomainEvent, IClock. No framework deps
  Modules/
    Identity/  Inventory/  Crm/  Sales/  Documents/  Signatures/
    Ai/  Ocr/  Messaging/  Publishing/  Reporting/  Billing/  Integrations/  Platform/
tests/
  <Module>.UnitTests/  <Module>.IntegrationTests/
  MautoDesk.Api.ContractTests/
  MautoDesk.SecurityTests/       cross-tenant probes, authz matrix, header assertions
  MautoDesk.ArchitectureTests/   NetArchTest — boundaries are a failing test, not a convention
```

Each module has four projects with strictly one-directional references:

| Project | May reference |
| --- | --- |
| `X.Domain` | `SharedKernel` only |
| `X.Application` | `X.Domain`, `X.Contracts` |
| `X.Infrastructure` | `X.Application` |
| `X.Contracts` | `SharedKernel` — this is the **only** project another module may reference |

## What `MautoDesk.ArchitectureTests` must assert

These are the invariants that keep the modular monolith from quietly becoming a ball of mud. Each is
a failing test, not a review comment:

1. No module references another module's `Domain`, `Application`, or `Infrastructure`.
2. `Domain` projects reference nothing but `SharedKernel`.
3. `Api` references no `Infrastructure` type outside DI registration.
4. `DateTime.Now` / `DateTime.UtcNow` appear nowhere outside the `IClock` implementation.
5. No `double` or `float` is reachable from `Sales.Domain`.
6. Every command/query handler has an explicit authorization policy — a handler without one fails a
   startup check.

## The three classes to write carefully

- **`TenantConnectionInterceptor`** — sets `app.tenant_id` transaction-locally on every connection.
  It is the single most security-critical class in the codebase (ADR-0002) and needs its own test
  suite, including a pooled-connection reuse test.
- **`DealEngine`** — pure, no I/O, no clock. Golden-file tests per state, validated against real
  dealer paperwork before that state goes live (ADR-0008).
- **`EnvelopeEncryptionService`** — AES-256-GCM with tenant ID and record ID bound as additional
  authenticated data, so ciphertext cannot be replayed into another tenant's row (ADR-0007).

## First vertical slice

Inventory + VIN decode + photos, end to end including auth and tenancy. It exercises every layer —
database, RLS, API, object storage, background jobs, frontend, tests — and produces something a
dealer can see on day one.
