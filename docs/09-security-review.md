# MautoDesk — Phase 9: Security Review

**Status:** Complete · **Phase:** 9 of 13 · **Reviewed against:** Phase 1 §6 controls, FTC Safeguards Rule

| Suite | Result |
| --- | --- |
| Unit | 32 / 32 |
| Architecture | 8 / 8 |
| Integration, auth, authorization, endpoint coverage, rate limiting | **58 / 58** |
| **Total** | **98 / 98** |
| Database isolation probe | 13 / 13 |
| RLS coverage gaps | 0 |
| .NET vulnerable packages | 0 |
| Frontend vulnerable packages | **0** (was 33, see F-1) |

---

## 1. How this review was done

Not by reading the code and agreeing with it. Every control below is marked with
what *evidence* supports it, and the review's job was to find the ones where the
honest answer was "nothing".

| Mark | Meaning |
| --- | --- |
| **Verified** | An automated test asserts it, and the test fails if the control is removed |
| **Observed** | Confirmed by hand against the running system; no regression guard |
| **Configured** | Present in code, not exercised by anything |
| **Absent** | Specified somewhere and not built |

The gap between *Configured* and *Verified* is where security regressions live: a
control that nothing tests is a control that survives until someone refactors
past it.

---

## 2. Findings

Ranked by what an attacker could actually do.

### F-1 — Critical RCE and 32 other advisories in frontend dependencies · **FIXED**

`pnpm audit` reported **1 critical, 16 high, 14 moderate, 2 low**. Several
applied directly to this application, not theoretically:

- **RCE in the React flight protocol** (critical) — App Router, which we use
- **Unauthenticated disclosure of internal Server Function endpoints** — login is
  a Server Action
- **SSRF in Server Actions on custom servers**
- **Middleware / proxy bypass in App Router** — auth gating relies on redirects

Nothing was watching. The dependency was pinned at Next 15.5.4 and CI had no
audit step.

**Fixed:** upgraded to Next 15.5.21, pinned `postcss >= 8.5.18` and
`sharp >= 0.35.0` through pnpm overrides. `pnpm audit` now reports zero, the app
builds, and **`pnpm audit --audit-level moderate` is a CI gate** so this cannot
recur silently.

### F-2 — No rate limiting anywhere · **FIXED**

`docs/04-api-contracts.md` §7 specifies composite limits. None existed. Account
lockout bounded guessing against *one* account; nothing bounded **credential
stuffing** — one password tried against thousands of accounts, never tripping a
single lockout.

**Fixed:** per-IP fixed window on `/auth/*` (10 per 15 min), per-user token
bucket on reads, per-tenant on writes. `RateLimitingTests` proves the limiter
actually refuses traffic rather than merely being configured.

### F-3 — Identity projects were absent from the solution · **FIXED**

`MautoDesk.Identity.*` were never added to `MautoDesk.sln`. They built
transitively via the API's project reference, so nothing looked wrong — but
`dotnet list package --vulnerable` **silently skipped the entire authentication
module, including Argon2, the JWT handler, and the TOTP library.** The audit
reported clean while never examining the code that handles passwords.

**Fixed:** all 14 projects are in the solution; the audit covers them.

### F-4 — Payload validated before resource existence · **FIXED**

`POST /vehicles/{id}/status` and `/price` validated the request body *before*
loading the vehicle. A caller could therefore distinguish "a vehicle I cannot
see" (422, about their input) from "no such vehicle" (404) by sending a
deliberately invalid body.

Not exploitable as written — the validation messages say nothing about the
vehicle — but the safety came from the messages happening to be harmless, not
from the design. That does not survive the next handler someone writes.

**Fixed:** existence is resolved first on both handlers. Found by the new
endpoint-enumeration probe, which is exactly the class of bug it was built for.

### F-5 — 429 responses were not `problem+json` · **FIXED**

The rate limiter set `Response.ContentType` before `WriteAsJsonAsync`, which
overwrote it with `application/json`. This broke the contract's "every non-2xx is
`application/problem+json`" rule, so a client branching on content type would
mis-handle a 429. Found by the test written alongside the limiter.

### F-6 — "Every route is probed" was aspirational · **FIXED**

`docs/02-architecture.md` §10 claimed every route is probed as tenant B with
tenant A's identifiers, and that "a new endpoint without a probe fails the
suite". In reality the probes were hand-written per endpoint — so a developer
adding an endpoint simply would not have written one, and nothing would have
noticed.

**Fixed:** `EndpointCoverageTests` enumerates the generated OpenAPI document and
probes every route automatically. It immediately found F-4.

### F-7 — Authorization matrix was never built · **FIXED**

Also promised in §10. `AuthorizationMatrixTests` now asserts role × endpoint
allow/deny across 16 combinations, including the negative cases that matter: a
user holding *some* inventory permission is still refused if it is the wrong one,
a user with none can sign in and do nothing, and permissions granted in one
tenant confer nothing in another.

### F-8 — Rate limiter partitions are in-process · **OPEN, accepted for now**

With more than one instance, an attacker gets N times the budget. Correct at one
instance, which is the launch topology. The seam for a Valkey-backed distributed
limiter is `RateLimiting.cs` alone. **Revisit before scaling past one instance** —
not before.

### F-9 — No MFA recovery path · **CLOSED**

Ten single-use codes are issued at enrolment, stored hashed, redeemable at
`POST /auth/mfa/recovery` against the challenge token from the password step,
and replaceable from account settings. A wrong code counts toward lockout; a
spent code and an unknown code return the same error, so neither confirms the
other. See `docs/08-authentication.md` §9 for the reasoning and
`MfaRecoveryTests` for the evidence.

The control no longer lacks a relief valve, and no administrator needs the
ability to switch off someone's second factor over the phone.

### F-10 — `RISK-SEC-001`: no hardware-backed KMS · **OPEN, known**

The envelope-encryption master key lives in the platform secret store because
DigitalOcean has no managed KMS. Compensating controls are real (per-record data
keys, tenant + record id bound as AAD, rotation runbook, restricted access), but
this remains a finding to close before a customer security review.
`IDataKeyProvider` makes it a provider swap.

### F-11 — xUnit 2.9.3 is marked deprecated · **OPEN, low**

`dotnet list package --deprecated` flags it as Legacy in favour of xunit.v3. No
vulnerability; a maintenance item.

---

## 3. Control coverage

Against the Phase 1 §6 groupings.

### Only the right people get in

| Control | State | Evidence |
| --- | --- | --- |
| Argon2id hashing | **Verified** | Production hasher used in every test login; parameters asserted |
| Rehash on parameter change | **Configured** | `NeedsRehash` implemented; no test forces an upgrade |
| MFA mandatory | **Verified** | `A_correct_password_alone_never_yields_tokens` |
| TOTP replay prevention | **Verified** | `A_totp_code_cannot_be_replayed_within_its_step` |
| Challenge-token purpose binding | **Verified** | `A_challenge_token_is_not_valid_for_a_different_purpose` |
| Account lockout, exponential | **Verified** | `Repeated_failures_lock_the_account` |
| Refresh rotation + reuse detection | **Verified** | `Replaying_a_rotated_refresh_token_revokes_the_entire_family` |
| No user enumeration (body) | **Verified** | Normalized problem bodies compared |
| No user enumeration (timing) | **Verified** | Median-latency comparison; catches a skipped Argon2 |
| Session revocation | **Verified** | `Logging_out_kills_the_session` |
| OAuth / SSO | **Absent** | Schema supports it; not implemented |
| WebAuthn | **Absent** | Schema supports it; TOTP only |

### They only see their own tenant

| Control | State | Evidence |
| --- | --- | --- |
| RLS enabled and forced on every tenant table | **Verified** | `app.rls_coverage_gaps()` = 0, asserted in CI |
| Fail-closed with no tenant context | **Verified** | Isolation probe check 1 |
| Cross-tenant read by primary key | **Verified** | Probe + `EndpointCoverageTests` across all routes |
| Cross-tenant write / forge / migrate | **Verified** | Isolation probe checks 3–5 |
| Pooled connection scope leakage | **Verified** | 12 alternating requests + 20 concurrent |
| Tenant from signed claim only | **Verified** | Forged-key token rejected; no header path exists |
| Privileged login lookups minimal | **Observed** | Two `SECURITY DEFINER` functions returning ids only; reviewed, not asserted by a test |

### They only do what their role allows

| Control | State | Evidence |
| --- | --- | --- |
| Permission checks in the application layer | **Verified** | `AuthorizationMatrixTests`, 16 combinations |
| Deny by default | **Verified** | `A_user_with_no_permissions_can_sign_in_and_do_nothing` |
| Permissions scoped per tenant | **Verified** | `Permissions_granted_in_one_tenant_do_not_apply_in_another` |
| Cost/gross hidden without permission | **Verified** | API omits fields; frontend verified both ways |
| Platform-admin separation | **Configured** | Distinct table and impersonation ledger; no code path yet |

### Data is unreadable if stolen

| Control | State | Evidence |
| --- | --- | --- |
| AES-256-GCM envelope encryption | **Configured** | Used for TOTP secrets; AAD binds tenant + record |
| TOTP secrets encrypted at rest | **Observed** | Confirmed in the database during verification |
| Refresh tokens stored hashed | **Verified** | Implicit: lookup is by SHA-256 and reuse detection works |
| PII field encryption (SSN, DL, bank) | **Absent** | Columns exist; CRM module not built |
| TLS everywhere | **Not applicable yet** | No deployed environment; Phase 12 |
| Secrets not in the repository | **Verified** | Tracked-file scan; only a test-fixture password |

### The app cannot be turned against the user

| Control | State | Evidence |
| --- | --- | --- |
| Security headers on every response | **Verified** | `Security_headers_are_present_on_every_route` |
| CSP | **Verified** | `default-src 'none'` — correct for a JSON API |
| Parameterized queries only | **Verified** | CA2100 as a build error + injection probes |
| Injection through sort/search inert | **Verified** | `Injection_attempts_through_query_parameters_are_inert` |
| No internals in error bodies | **Verified** | `No_response_leaks_stack_traces_or_sql`, 6 hostile probes |
| Browser never holds a token | **Observed** | Verified end to end; no JWT in rendered HTML |
| Cookie `HttpOnly` + `SameSite=Lax` + encrypted | **Configured** | No automated assertion |
| CORS allowlist | **Absent** | Not configured; BFF means no browser origin calls the API today |
| CSRF | **Partially absent** | `SameSite=Lax` blocks the cross-site POST; no token-based defence. Acceptable while the API is bearer-only, **must be revisited if any cookie-authenticated endpoint is added** |

### Uploads cannot hurt us

| Control | State | Evidence |
| --- | --- | --- |
| Entire pipeline | **Absent** | Photos and documents are not built. ADR-0005 is designed, not implemented |

Stated plainly because the constitution lists many upload controls and it would
be easy to read the design as an implementation.

### Abuse is bounded

| Control | State | Evidence |
| --- | --- | --- |
| Per-IP auth rate limiting | **Verified** | `Login_is_rate_limited_by_address` |
| Per-user / per-tenant limits | **Configured** | Implemented; no test drives them to exhaustion |
| `Retry-After` on 429 | **Verified** | `A_rejected_request_carries_retry_after_and_problem_details` |
| Idempotency keys | **Absent** | Specified in the contract; not implemented |
| AI cost caps | **Absent** | AI module not built |

### We can prove what happened

| Control | State | Evidence |
| --- | --- | --- |
| Append-only audit ledger | **Verified** | Isolation probe: UPDATE and DELETE both rejected |
| Hash-chained, tamper-evident | **Verified** | `audit.verify_chain()` asserted |
| Login attempts recorded, including unknown users | **Observed** | Written via `SECURITY DEFINER`; no assertion |
| Domain events → outbox, transactional | **Verified** | Outbox row asserted on vehicle creation |
| Application audit events for business actions | **Absent** | Ledger exists; handlers do not write to it yet |
| PII redaction in logs | **Absent** | Serilog destructuring policy not implemented |

---

## 4. FTC Safeguards posture

We are a **service provider** to dealers, who are the regulated financial
institutions. They will send us a vendor questionnaire.

| Requirement | Where we stand |
| --- | --- |
| MFA for everyone accessing customer information | ✅ Mandatory, no opt-out |
| Encryption in transit | ⬜ Phase 12 — no deployed environment |
| Encryption at rest | ⚠️ Database-level available; field-level implemented only for TOTP secrets |
| Access controls / least privilege | ✅ RBAC verified; app role has no `BYPASSRLS` |
| Activity monitoring | ⚠️ Login attempts recorded; business-action audit not wired |
| Secure disposal | ⬜ Retention policy engine designed, not built |
| Change management | ✅ CI gates on schema, contract, tests, dependencies |
| Vendor oversight | ⬜ Not applicable until we have vendors |
| Written information security programme | ⬜ Not started |
| Annual penetration test, biannual vulnerability assessment | ⬜ Scheduled obligation, not yet due |
| Incident response plan | ⬜ Not started |

> ⚠️ **Not legal advice.** This is an engineering assessment. A dealer's
> questionnaire will ask questions this table does not answer, and counsel must
> review before we sign one.

---

## 5. What I would fix next, in order

1. ~~**F-9 — MFA recovery codes.**~~ Done — see F-9 above.
2. ~~**Audit events for business actions.**~~ Done — inventory and photo writes
   record entries in the same transaction as the change (`docs/06-backend.md` §10).
3. ~~**PII log redaction.**~~ Done — attribute-driven redaction plus pattern
   scrubbing, wired into the logging pipeline rather than left to call sites.
4. **Idempotency keys.** Specified, and money-adjacent endpoints are coming.
5. **F-8 — distributed rate limiting**, when the topology needs it and not before.

---

## 6. What this review does not cover

- **No third-party penetration test.** This is a self-assessment. FTC Safeguards
  requires an independent test annually, and a self-review is not a substitute.
- **No DAST run.** OWASP ZAP against staging is in the CI design; there is no
  staging environment yet.
- **No load or DoS testing.** Rate limits are asserted functionally, not under
  load.
- **No review of unbuilt modules.** Deals, documents, signatures, OCR, messaging,
  and publishing carry the majority of the remaining risk — in particular the
  e-signature evidence package (`RISK-LEGAL-001`) and driver's-licence retention
  (`RISK-LEGAL-002`). Both need legal review, not only engineering review.
