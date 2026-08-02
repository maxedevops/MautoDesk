# MautoDesk — Phase 8: Authentication

**Status:** Complete and verified · **Phase:** 8 of 13

| Suite | Result |
| --- | --- |
| Unit | **32 / 32** |
| Architecture | **8 / 8** |
| Integration + auth security | **34 / 34** |
| Database isolation probe | 13 / 13 |
| RLS coverage gaps | 0 |

The dev-header shim is **gone**. Tenancy now comes from a signed JWT claim, which
is what ADR-0002 always specified.

---

## 1. The decisions, and why

| Decision | Reasoning |
| --- | --- |
| **Argon2id**, m=19456 KiB, t=2, p=1 | OWASP's first choice and current minimum. Memory-hard against GPU cracking; the hybrid first pass resists side channels. Parameters are stored *in* the hash, so raising them later upgrades users silently on next login instead of forcing a password reset |
| **MFA is mandatory, not optional** | FTC Safeguards requires MFA for everyone with access to customer information. So there is no "enable MFA" setting to leave off — a user without a factor is sent to enrolment and **cannot complete a login** until they finish |
| **Access tokens: HS256, 15 min** | The API is the only verifier, so a symmetric key avoids key distribution. Becomes RS256 by configuration the day a second service needs to verify independently |
| **Permissions embedded in the token** | Authorization becomes a signature check, not a database read per request. Cost: a revoked permission stays effective until the token expires — bounded at 15 minutes, and re-read on every refresh. Immediate revocation still works, because it revokes the *session*, which kills the next refresh |
| **Refresh tokens: opaque, 32 random bytes, stored as SHA-256** | They carry no claims anyone needs to read, so a signed document buys nothing. A stolen database yields no usable tokens |
| **Rotation with reuse detection** | See §2 |
| **TOTP window: ±1 step** | 30 seconds of tolerance covers phone clock drift and typing. Wider windows are common and wrong: each extra step linearly increases the guess space |
| **`ClockSkew = TimeSpan.Zero`** | The .NET default of five minutes would stretch a 15-minute token to 20 and keep a revoked session alive a third longer than intended |
| **`ValidAlgorithms = [HmacSha256]`** | Without an explicit allow-list, a token presenting a weaker or `none` algorithm can be honoured by permissive validators |

---

## 2. Refresh rotation and reuse detection

Rotation alone does **not** stop token theft. If an attacker steals a refresh
token and redeems it first, they silently take over the session and the victim's
next refresh just looks like an ordinary expiry.

So a token that has already been rotated is treated as proof that two parties
hold it. Since we cannot tell victim from thief, **the entire family is
revoked** — both are logged out and must authenticate again. Theft becomes loud
instead of silent.

`Replaying_a_rotated_refresh_token_revokes_the_entire_family` asserts exactly
this, including that the *legitimate* client's newer token also dies.

---

## 3. No user enumeration

An unknown address and a wrong password return the **same status, the same body,
and roughly the same timing**. Otherwise the login endpoint becomes a directory
of who works at a dealership — a ready-made phishing list.

The timing half matters as much as the body. Without work on the unknown-user
path, login returns in microseconds for an unknown address and ~50 ms for a real
one, which is trivially measurable over the internet. `AuthenticationService`
therefore verifies against a **decoy hash** when no account exists, so both paths
pay the Argon2 cost. Two tests cover it: one comparing normalized bodies, one
comparing median latency.

**One deliberate exception:** lockout *is* reported. By that point the caller has
already supplied a correct-looking address, so enumeration is moot, and a
locked-out dealer who is told nothing will phone support instead.

---

## 4. The tenant boundary at login

Login has a genuine chicken-and-egg problem: RLS needs a tenant, and the tenant
is derived *from* the user being authenticated. Rejected alternatives:

- **`BYPASSRLS` on the application role** — hands the whole application a master
  key to every tenant's data to solve one query.
- **A second superuser connection** — the same thing, slightly better hidden.
- **A tenant in the login request** — precisely the caller-supplied tenancy
  ADR-0002 forbids, and it lets an attacker probe which tenant an address is in.

Instead, two `SECURITY DEFINER` functions, each as narrow as possible:

| Function | Returns | Used by |
| --- | --- | --- |
| `identity.find_user_for_authentication(citext)` | **user id and tenant id, nothing else** | `/auth/login` |
| `identity.find_refresh_token_tenant(bytea)` | **tenant id only**, from a token *hash* | `/auth/refresh`, `/auth/logout` |

Everything else — the password hash, lockout state, MFA factors — is read back
through the ordinary tenant-scoped path once the tenant is known. The
cross-tenant surface is two columns wide, `search_path` is pinned on both, and
`EXECUTE` is granted only to `mautodesk_app`.

The MFA endpoints have the same shape of problem (a challenge token, no bearer
token) and are solved without any privileged function at all: the tenant comes
from the **signed** challenge token.

---

## 5. What the tests actually attack

`AuthenticationSecurityTests` is written from the attacker's side, because a
login flow with a stolen-token hole behaves perfectly for every honest user.

- Replaying a rotated refresh token → whole family revoked
- Reusing a refresh token → rejected
- Unknown address vs wrong password → identical body, comparable timing
- Correct password alone → **never** yields tokens
- A TOTP code replayed inside its 30-second step → rejected
- An *enrolment* challenge presented to *verification* → rejected (without the
  purpose check this defeats mandatory MFA entirely)
- Five failed attempts → account locked
- Tampered signature → 401
- Token signed with a different key, carrying a valid-looking tenant claim → 401
- A real token reaching another tenant's vehicle → 404

---

## 6. Bugs found while building this

Recorded because each one is a reason a test exists.

1. **Claim mapping.** `JwtSecurityTokenHandler` rewrites `sub` to a long-form
   .NET claim URI by default, so challenge validation silently failed. Fixed by
   clearing `InboundClaimTypeMap` — and the bearer handler is configured to
   match, so both read the raw claim names the tokens carry.
2. **Enrolment saved too early.** The login path committed *before*
   `BeginEnrolmentAsync` created the pending factor, so a user was handed a TOTP
   secret that was never persisted and confirmation found nothing.
3. **EF insert ordering, twice.** No declared relationship meant EF inserted a
   refresh token before its session, and updated a rotated token before
   inserting its successor. Both fixed by declaring the foreign keys without
   navigation properties, keeping the aggregate boundary intact.
4. **`inet` columns.** Npgsql maps `inet` to `IPAddress`, not `string`. Fixed
   with a value converter — and it exposed that `CF-Connecting-IP` is
   caller-supplied, so an unparseable header would have turned a login into a
   500. Now parsed and validated before use.
5. **A swallowed redirect.** The inventory page's `catch` was eating Next's
   `redirect()` signal, so an unauthenticated visitor got a rendered page with an
   error note instead of the login screen.

---

## 7. The BFF session

`apps/web` holds the tokens; the browser gets one sealed `HttpOnly` cookie.

The cookie is **encrypted** (AES-256-GCM), not merely signed. Signing prevents
tampering but leaves tokens readable by anything that can read the cookie jar —
a browser extension, a shared machine, a backup. Encrypting makes the cookie
inert without the server key.

Verified end to end against the running stack: password → mandatory enrolment →
TOTP → sealed cookie → rendered inventory, with **no JWT anywhere in the HTML**.

`SESSION_SECRET` must decode to exactly 32 bytes and the app refuses to start
otherwise — a check that caught a 34-byte key during this very verification.

---

## 8. Known gaps

| Gap | Notes |
| --- | --- |
| **The login form is not covered by an automated test** | It is a Next Server Action, which cannot be driven by a plain HTTP POST. The API flow and the session layer are both covered; the form plumbing is verified manually. A Playwright test is the right fix |
| No rate limiting yet | Lockout bounds per-account guessing, but there is no per-IP throttle. Cloudflare covers the edge in deployed environments; the API-side limiter from `docs/04-api-contracts.md` §7 is not built |
| No password reset or invitation flow | Users are seeded. Needed before anyone but us can onboard |
| No WebAuthn | The schema supports it; only TOTP is implemented |
| Recovery codes cannot be re-shown | Built (see §9), but the codes are stored hashed, so a user who loses the printout must generate a new set rather than retrieve the old one. That is the intended trade; it is listed here because support will be asked |
| Sessions are not listed or revocable in the UI | `/auth/sessions` is specified in the design contract but not implemented |
| `RISK-SEC-001` still open | The envelope-encryption master key lives in configuration; DigitalOcean has no managed KMS |

---

## 9. Recovery codes

MFA is mandatory, so the system needs an answer for the phone that ends up in a
car wash. The alternative to a recovery code is an administrator disabling
someone's second factor because a voice on the phone asked — which is the
social-engineering path the whole control exists to close.

**The shape of it:**

- **Ten codes, issued at enrolment**, not offered as a later opt-in. A code the
  user never generated is worth nothing on the day they need it.
- **Shown exactly once.** They are stored as SHA-256 hashes, so there is nothing
  to retrieve later. Losing them means generating a new set.
- **Unsalted digest, deliberately.** A code is ~49 bits of randomness *we*
  generated, not a human-chosen password, so a slow KDF defends nothing and an
  unsalted digest keeps redemption a single indexed lookup.
- **A second factor, not a bypass.** `POST /auth/mfa/recovery` requires the
  signed challenge token from the password step, exactly as `mfa/verify` does.
- **Single use, enforced in the domain.** `MfaRecoveryCode.Redeem` refuses a
  second redemption; the row records when it was spent.
- **A wrong code costs a lockout attempt**, the same as a wrong TOTP code —
  otherwise this endpoint is simply the cheapest way to guess.
- **Regeneration discards the old set** in the same transaction that writes the
  new one, so a discarded printout stops working.
- **The session records `amr: ["pwd","recovery"]`**, so an auditor can tell which
  logins skipped the authenticator.

The alphabet omits `0/O` and `1/I/L/U`. That is not fussiness: these are read off
paper, over the phone, by someone already having a bad day.

Covered by `MfaRecoveryTests` (single use, cross-user rejection, regeneration
invalidating the old set, formatting tolerance, and the requirement for a
challenge token) and `RecoveryCodeServiceTests`.
