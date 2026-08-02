# MautoDesk — Phase 4: API Contracts

**Status:** Draft for review · **Phase:** 4 of 13
**Machine-readable contract:** `contracts/openapi.yaml` (OpenAPI 3.1)

This document states the rules every endpoint obeys. The YAML states the endpoints. Where they
disagree, the YAML wins — it is what generates the client.

---

## 1. The contract pipeline

```
ASP.NET Core endpoints + XML docs
        ↓ (build)
contracts/openapi.yaml           ← generated artifact, committed
        ↓ (CI)
packages/api-client/ (TypeScript types + fetch client + Zod schemas)
        ↓
apps/web
```

**The committed YAML is checked in CI against the one the build produces.** If a developer changes an
endpoint without regenerating, the build fails. This is what makes ADR-0010's promise real: a
front-end/back-end type mismatch is a red build, never a runtime `undefined`.

The hand-authored YAML in this repository today is the **design-time contract** — it is what the
backend must implement in Phase 6. Once the API exists, generation replaces authorship and the file
becomes read-only to humans.

---

## 2. Versioning

- URL-versioned: `/api/v1/...`. Blunt, obvious in logs, and trivially routable at the edge.
- **v1 is additive-only.** New optional fields, new endpoints, new enum values are fine. Removing a
  field, renaming one, tightening validation, or changing a default is a v2 change.
- Clients must ignore unknown response fields. This is stated in the contract so it is a
  documented expectation rather than a hope.
- Deprecation: `Deprecation` and `Sunset` response headers, minimum six months of overlap, and the
  deprecated operation stays in the OpenAPI doc marked `deprecated: true` — removing it from the doc
  is how integrators find out too late.

---

## 3. Authentication

| Consumer | Mechanism |
| --- | --- |
| The web app | BFF pattern: browser holds an `HttpOnly; Secure; SameSite=Lax` session cookie; the Next.js server holds the tokens and sends `Authorization: Bearer` to the API. The browser never sees a JWT, so XSS cannot exfiltrate one |
| Third-party / tenant integrations | `Authorization: Bearer <access_token>` obtained via the token endpoint, or `X-API-Key` for tenant-issued keys with a fixed scope set |
| Public feed (`/public/v1`) | Anonymous, per-tenant feed token in the path. Serves only published inventory. Contains no PII, ever |

**Tenancy is resolved from the token's `tenant` claim and from nothing else.** There is no
`X-Tenant-Id` header in this API and there never will be — see ADR-0002. A subdomain may route a
request; it may not authorize one.

**Token lifetimes.** Access 15 minutes, refresh 30 days, rotating with reuse detection. A replayed
refresh token revokes the entire family and raises a security event.

**MFA is a platform requirement, not a tenant option** (FTC Safeguards). `POST /auth/login` returns
`mfa_required` with a challenge token rather than tokens when a second factor is outstanding.

---

## 4. Errors: RFC 9457 problem details, always

Every non-2xx response is `application/problem+json`. No endpoint returns a bare string, an HTML
error page, or a 200 with `{"success": false}`.

```json
{
  "type": "https://api.mautodesk.com/problems/validation-failed",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "detail": "The request could not be processed.",
  "instance": "/api/v1/vehicles",
  "traceId": "0af7651916cd43dd8448eb211c80319c",
  "errors": {
    "vin": ["VIN must be exactly 17 characters.", "VIN contains invalid characters I, O or Q."],
    "listPrice": ["List price cannot be negative."]
  }
}
```

| Status | When |
| --- | --- |
| 400 | Malformed request — unparseable body, bad query type |
| 401 | Missing, expired, or invalid credentials |
| 403 | Authenticated, but the principal lacks the required permission |
| **404** | **Not found *or* belongs to another tenant.** These are deliberately indistinguishable — a 403 for a cross-tenant ID would confirm the record exists |
| 409 | Optimistic-concurrency conflict (stale `If-Match`), or a business-rule conflict such as a duplicate stock number |
| 410 | Resource was deleted |
| 415 | Unsupported media type |
| 422 | Well-formed but semantically invalid — the common validation failure |
| 423 | Account locked |
| 429 | Rate limited; `Retry-After` is always present |
| 5xx | Our fault. **Never** includes a stack trace, SQL, or internal identifiers — only the `traceId`, which support can correlate |

`traceId` is the W3C trace ID and appears in every response, success or failure.

---

## 5. Collections

```
GET /api/v1/vehicles?status=available&make=Ford&page=1&pageSize=50&sort=-acquiredAt&q=f150
```

```json
{
  "items": [ { "...": "..." } ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 187,
  "totalPages": 4
}
```

- `pageSize` defaults to 25, maximum 100. A client asking for more gets 100, not an error, and not
  the whole table.
- `sort` takes a comma-separated field list; `-` prefixes descending. Only whitelisted fields are
  sortable — the sort parameter never reaches SQL as a string.
- `q` is full-text search across the entity's `search_vector`.
- Filters are explicit named parameters, not a generic query language. A generic filter DSL is an
  injection surface and an unindexable-query generator; explicit parameters map to indexes we
  actually built (`docs/03-database-design.md` §6).
- **Cursor pagination** (`cursor` + `nextCursor`) is offered on high-volume, append-only collections
  — activity, audit, messages — where deep offset paging degrades. Offset paging stays for grids
  where users jump to page 7.

---

## 6. Idempotency and concurrency

**Idempotency.** `POST` endpoints that create money-adjacent or externally-visible resources —
deals, payments, signature envelopes, outbound messages — accept `Idempotency-Key`. A repeat of the
same key with the same request body returns the original response; the same key with a *different*
body returns 409. Keys are retained 24 hours in `app.idempotency_key`.

**Concurrency.** Mutable resources return `ETag`; `PATCH`/`PUT` require `If-Match`. A stale value
returns 409 with the current representation, so the client can show the user what changed rather
than silently overwriting a colleague's edit. This maps to the `xmin` concurrency token.

---

## 7. Rate limiting

Composite limits, most restrictive wins. Cloudflare enforces the outer edge limit; the API enforces
the tenant- and user-aware ones it can see.

| Bucket | Limit |
| --- | --- |
| Per IP, unauthenticated | 60 req/min |
| `POST /auth/login`, per IP + per account | 10 / 15 min, then exponential backoff |
| Per user, authenticated | 600 req/min |
| Per tenant | 3,000 req/min |
| Write endpoints, per tenant | 300 req/min |
| AI generation, per tenant | Plan quota, enforced *before* the model call (ADR-0004) |
| Public feed, per tenant | 120 req/min |

Responses carry `RateLimit-Limit`, `RateLimit-Remaining`, `RateLimit-Reset`; 429 always carries
`Retry-After`.

---

## 8. Long-running operations

Anything bound by a third party or a model is asynchronous. This is what keeps the *interactive*
p95 inside 200 ms while VIN decode, OCR, AI generation, image processing, syndication, and report
export take as long as they take.

```
POST /api/v1/vehicles/{id}/ai/description   → 202 Accepted
                                              Location: /api/v1/jobs/{jobId}
GET  /api/v1/jobs/{jobId}                   → { status: queued|running|succeeded|failed, result }
```

Clients poll the job or subscribe to the SSE stream at `/api/v1/events` (Release 2). The 202 carries
a `retryAfterSeconds` hint so clients do not poll tighter than useful.

---

## 9. File upload

Three steps, because the server must never proxy an unscanned file and the client must never be
trusted (ADR-0005):

```
POST /api/v1/uploads/intent      → presigned PUT to the quarantine bucket, short TTL,
                                   size- and content-type-constrained
PUT  <presigned url>             → client uploads directly to R2
POST /api/v1/uploads/{id}/confirm→ 202; enqueues scan → magic-byte verification → hash check →
                                   ClamAV → image re-encode (strips EXIF/GPS) → promote
```

Declared `contentType`, `byteSize`, and `sha256` in the intent are **checked against the actual
object**, not trusted. Nothing user-uploaded is ever served from the application origin.

---

## 10. Sensitive fields

- SSN, driver's licence number, and bank/routing numbers are **write-only** in the contract
  (`writeOnly: true`). They can be submitted; they are never returned.
- Reads return masked forms only: `ssnLast4`, `dlNumberMasked`.
- Unmasking is a separate, audited operation — `GET /api/v1/customers/{id}/sensitive` — gated on
  `crm.customer.pii.read`, which writes an audit event **every time**, including the reason.
- These fields carry `x-sensitive: true` in the OpenAPI doc, which the log-redaction policy and the
  generated client both consume. Redaction is opt-out, not opt-in.

---

## 11. Naming

- JSON is `camelCase`; the database is `snake_case`; the mapping happens once, in serialization
  configuration, not per-DTO.
- Paths are lowercase plural nouns: `/vehicles`, `/customers`, `/deals`.
- Sub-resources nest one level only: `/vehicles/{id}/photos`. Beyond that, use a top-level resource
  with a filter — `/deals?vehicleId=...` rather than `/vehicles/{id}/deals`.
- Actions that are genuinely not CRUD are a `POST` to a verb sub-path:
  `POST /deals/{id}/calculate`, `POST /envelopes/{id}/send`, `POST /vehicles/{id}/publish`.
- Money is an object, never a bare number: `{ "amount": "28995.00", "currency": "USD" }`, with the
  amount as a **string** so no JavaScript client can round it through a float. This is the same
  reason the database uses `numeric` and the backend uses `decimal`.

---

## 12. What Phase 5 (UI/UX) needs from this

1. Every list endpoint's filter set, so grid filter chips match what the server can actually index.
2. The job/polling contract, so async operations get real progress affordances rather than spinners
   that never resolve.
3. The permission code on every operation (`x-permission` in the YAML), so the UI can hide what a
   user cannot do — while the server still enforces it, because a hidden button is not a control.
