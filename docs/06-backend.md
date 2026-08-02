# MautoDesk — Phase 6: Backend

**Status:** First vertical slice complete and green · **Phase:** 6 of 13
**Stack:** ASP.NET Core 9.0.18 on .NET 9.0.316

| Suite | Result |
| --- | --- |
| Unit tests | **32 / 32** |
| Architecture tests | **8 / 8** |
| Integration tests (real PostgreSQL, full HTTP stack) | **16 / 16** |
| Release build | 0 warnings, 0 errors (warnings are errors) |

---

## 1. What was built, and why this slice

Phase 6 implements the vertical slice recommended in Phase 1 §11: **Inventory, end to end, with the
tenancy machinery underneath it.** Database → RLS → repository → application layer → HTTP → tests.

That choice is deliberate. Inventory is the shallowest module in the product, but the slice through
it touches every layer that matters and forces the hard problems early:

- the connection interceptor that makes row-level security work,
- the outbox that makes "enter data once" survive a crash,
- the permission model,
- the error contract,
- the read/write split,
- and an external provider that will be down sometimes.

A "user management first" slice would have produced more screens and answered none of those.

**Not built in this phase:** authentication (Phase 8), photos, costs, CRM, deals. The deal engine in
particular is deliberately untouched — it is the highest-risk component in the system and it should
be written after the patterns here have been reviewed, not alongside them.

---

## 2. Structure

```
backend/
  MautoDesk.sln
  Directory.Build.props        net9.0, nullable, warnings-as-errors, analyzers
  Directory.Packages.props     central version pinning
  src/
    MautoDesk.SharedKernel/    Money, Result, Entity/AggregateRoot, IClock, ITenantContext
    MautoDesk.Infrastructure/  DbContext, outbox, TenantConnectionInterceptor
    Modules/Inventory/
      Domain/                  Vehicle aggregate, Vin, StockNumber, Mileage, events
      Contracts/               the module's ONLY public surface
      Application/             ports + command/query handlers
      Infrastructure/          EF config, repository, read store, NHTSA decoder
    MautoDesk.Api/             minimal API, problem details, security headers, health
  tests/
    MautoDesk.UnitTests/
    MautoDesk.ArchitectureTests/
    MautoDesk.IntegrationTests/
```

Module reference rules are enforced by `MautoDesk.ArchitectureTests`, not by convention.

---

## 3. The three classes that carry the design

### 3.1 `TenantConnectionInterceptor` — the one to review carefully

Every RLS policy compares `tenant_id` against `app.current_tenant_id()`, which reads a session
variable this class sets. If it sets the wrong tenant, one dealership reads another's customers.

**A correction worth recording.** The obvious implementation is
`set_config('app.tenant_id', $1, true)` — transaction-local, therefore self-cleaning. That is
correct *only inside an explicit transaction*. EF Core issues plenty of reads with no transaction
open, and outside one a "transaction-local" setting applies to that single statement and then
vanishes — so the next query in the same request sees no tenant and returns nothing.

The implementation therefore sets it **session-local** and clears it on
`ConnectionClosingAsync`. The reset is the half that is easy to omit and impossible to notice in
manual testing, which is exactly why
`Alternating_tenants_across_pooled_connections_never_leaks_scope` exists and alternates twelve
times rather than once.

**Fail-closed:** with no tenant in scope the variable is set to empty, `app.current_tenant_id()`
returns NULL, every predicate evaluates to NULL, and the query returns zero rows.

### 3.2 `Money` — why `decimal`, and why rounding is named

128-bit base-10. There is no implicit conversion from `double`, and an architecture test fails the
build if a `double` becomes reachable from a domain or contract type.

Two behaviours are worth stating because both are non-default:

- **Rounding is half-away-from-zero, not banker's rounding.** .NET's default `MidpointRounding.ToEven`
  would round $0.125 to $0.12, which does not match how a dealer's paperwork or a state tax table
  rounds a half cent.
- **`TryParse` rejects group separators.** Under a permissive parse `"28,995.00"` becomes `28.995` —
  a $28,995 truck priced at twenty-nine dollars. Failing loudly is the only safe behaviour, and there
  is a test for it.

### 3.3 `Vehicle` — permissive on save, strict on publish

Almost every field is optional. A salesperson standing on a lot has a VIN and a stock number; they
do not have the trim level or the recon cost. A DMS that refuses to save until eleven fields are
filled loses to a notebook, so completeness is *reported* (`GetPublishReadiness`) rather than
enforced.

Strictness moves to where it earns its keep:

- **Publishing requires a photo and a price.** A listing with neither is one shoppers bounce off,
  and it damages the dealer's placement on the marketplace.
- **`ApplyDecode` fills gaps and never overwrites dealer input.** If someone corrected the trim from
  the window sticker, a later decode must not silently undo that.
- **An AI draft is not a description.** `ProposeAiDescription` and `ApproveAiDescription` are
  separate, named operations — the mechanical guarantee behind ADR-0004.
- **The VIN freezes once sold**, because it identifies the unit on a signed contract and a title
  application.

---

## 4. Decisions taken during implementation

| Decision | Reasoning |
| --- | --- |
| No MediatR | Handlers are plain classes registered in DI. MediatR earns its place with many cross-cutting behaviours; with two handlers it is indirection without benefit. Revisit when a pipeline is genuinely needed |
| `Result<T>`, not exceptions, for expected failures | A duplicate stock number is a business outcome, not an exceptional one. Keeping it in the type signature means a handler cannot silently forget the failure path |
| Authorization in the application layer | An HTTP request is one caller; a job, an import and an internal call are others. A check on the endpoint protects one of four paths |
| Repository has no `WHERE tenant_id` | Deliberate. RLS is the authority. An application predicate would invite the belief that *it* is what protects us, and the day someone omits it that belief becomes a breach |
| Read store separate from repository | Rendering a 500-row grid must not materialize 500 aggregates |
| Custom health check | `AddDbContextCheck` answers "can I connect". The question that matters is "am I connected as a role that *cannot* bypass RLS" — a superuser connection string would pass a connectivity check while disabling every isolation policy |
| VIN decoder never throws | A dealer must be able to book a car whether or not a government API is up. A timeout returns `Unavailable` and the flow continues with manual entry |

---

## 5. Bugs the tests caught

Recorded because they are the reason the suites exist.

1. **`SqlQueryRaw` scalar shape.** EF wraps a scalar query in `select s."Value" from (...) as s`, so
   the projected column must literally be named `"Value"`. The photo-count query returned an unnamed
   column and produced 500s on create and publish. Caught by integration tests, invisible to unit
   tests.
2. **An N+1 in the inventory grid.** The first draft counted photos with a correlated subquery per
   row — precisely the shape the performance budget forbids, in precisely the place it hurts most.
   Replaced with one grouped query per page.
3. **A fragile test assertion.** The isolation tests originally asserted on stock-number prefixes,
   which broke as soon as other tests added rows with different naming. Rewritten to compare against
   actual tenant ownership read from the database — both pollution-proof and a stronger statement of
   the property under test.

---

## 6. Known gaps

Stated plainly rather than left to be discovered.

| Gap | Plan |
| --- | --- |
| **No real authentication.** A development-only header sets the tenant | Phase 8. Guarded three ways: registers only outside Production, requires `DevAuth:Enabled`, and the app **refuses to start** if both are somehow true in Production |
| Photos, costs and recon are schema-only | Next slice |
| No outbox dispatcher yet | Messages are written correctly and transactionally; nothing consumes them. Hangfire host is Phase 6b |
| No model/schema drift test | ADR-0011 promises one. The schema is SQL-first, so a drift test is required before more modules are mapped |
| No Redis, no caching | Not needed at this size; adding it before there is a measured problem would be speculative |
| Concurrency token mapped but untested | `xmin` is configured; no test yet proves a stale write is rejected |

---

## 7. Running it

```bash
docker compose up -d postgres && docker compose run --rm migrate
```

Give the application role a password (the compose migration does not, deliberately — production sets
it from a secret):

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_app with password 'devpw';"
```

Then:

```bash
dotnet test backend/MautoDesk.sln -c Release
```

Connection strings default to the compose setup and are overridable with `TEST_APP_CONNECTION` and
`TEST_ADMIN_CONNECTION`, which is how CI points them at its own service container.

---

## 8. What Phase 7 needs from this

1. `contracts/openapi.yaml` is still hand-authored. Generating it from these endpoints and failing
   CI on drift (ADR-0010) should happen **before** the frontend generates a client from it.
2. `VehicleDto.Readiness` is the data behind the completeness meter in `docs/05-ux-design.md` §5.1.
3. Cost and gross fields are absent from the DTOs entirely rather than nulled — matching the
   "permission-shaped, not permission-broken" rule in §6 of the UX doc.
4. Money crosses the wire as a decimal string. The client must format it, never parse it into a
   JavaScript number.

---

## 9. Photo uploads

Implements ADR-0005's quarantine-first flow. Three calls, and the middle one does not touch this
API at all:

1. `POST /vehicles/{id}/photos` — the client declares content type, byte size, and a SHA-256. A row
   is created in `pending` and a presigned PUT URL for the **quarantine** bucket comes back, good
   for 15 minutes and signed with the declared type and length.
2. The client PUTs the file straight to storage. A 20 MB photo never crosses the request pipeline.
3. `POST /vehicles/{id}/photos/{photoId}/confirm` — verification, in order, each failure a rejection
   with a reason rather than an exception: the object exists → its length matches what was declared
   → its SHA-256 matches → the malware scanner passes → it decodes as an image. The decoded image is
   re-encoded to JPEG at two sizes and written to the **media** bucket; the quarantine object is
   deleted either way.

**The re-encode is the security control.** Writing a fresh file from a pixel buffer discards EXIF
(including the GPS coordinates of the lot, and sometimes of someone's house), colour-profile
payloads, appended archives, and the polyglot files that are a valid JPEG *and* a valid script.
Nothing that was not pixels survives it.

**Deliberate deviations from the ADR sketch,** both recorded rather than hidden:

- **Verification runs inline on confirm, not in a background job.** The outbox dispatcher does not
  exist yet, and a photo stuck in `processing` because nothing consumes the queue is worse than a
  confirm call that takes a second. `PhotoCommandHandler.ProcessAsync` is already shaped like a job
  body; moving it later changes the caller, not the logic.
- **Scanning fails closed.** `ClamAvScanner` throws when clamd is unreachable and
  `MalwareScanning:Required` is true, which is the default. Setting it false is a deliberate act for
  a machine with no clamav container, and the verdict then reads `not-scanned` rather than claiming
  the file was checked and found clean.

Only a `ready` photo counts toward publish readiness, and only a `ready` photo is given a URL.
`PhotoUploadTests` covers the happy path, a digest mismatch, a text file declared as a JPEG, a
confirm with nothing uploaded, cross-tenant access, and the single-primary rule.

---

## 10. The audit ledger and log redaction

### What gets written

`IAuditLog.Record` adds an entry to the caller's **unit of work**. It is not a
separate write: the entry lands in the same transaction as the change it
describes, so a refused operation cannot leave a record claiming it happened,
and a committed one cannot be missing its record. `AuditLedgerTests` asserts
both directions.

Recorded today: vehicle created, status changed, price changed, published, and
deleted; photo added, rejected, and deleted. A price change stores both numbers
**as strings** — a JSON number would round a price through a double on its way
into the record that exists to be trusted.

`prev_hash` and `hash` are deliberately absent from the entity. A BEFORE INSERT
trigger computes them from the row and its predecessor, so the chain attests to
what was stored rather than to what the application claimed; a compromised
application cannot write a consistent chain of lies. `update` and `delete` are
revoked from `mautodesk_app` and blocked by a trigger — asserted from the
application role's own connection.

### What gets redacted

Two mechanisms, because they fail differently:

- **`[Sensitive]`** marks a property at its definition. Serilog's destructuring
  policy replaces it with `[redacted]` before the value is ever written, and the
  OpenAPI document emits `x-sensitive: true` for the same property from the same
  attribute — so the contract cannot promise care that the logger does not take.
  A name list (`password`, `ssn`, `accountNumber`, …) backstops objects nobody
  could attribute: anonymous types, third-party models.
- **Pattern scrubbing** handles free text — an exception quoting a row, a
  Postgres error echoing a parameter, a URL carrying a token. Social security
  numbers, card numbers, bearer tokens, and JWTs are masked; email addresses
  keep their domain and lose the local part, so "which dealership was this?"
  stays answerable while the individual does not appear.

It is a net, not a proof. It does not make logging a customer object acceptable,
and the CRM module should not treat it as permission to try.
