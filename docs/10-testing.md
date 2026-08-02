# MautoDesk — Phase 10: Testing

**Status:** Complete · **Phase:** 10 of 13

| Suite | Count | Runs against |
| --- | --- | --- |
| Backend unit | **63** | Nothing external |
| Architecture | **8** | Compiled assemblies |
| Backend integration | **58** | Real PostgreSQL, real HTTP stack |
| Frontend unit | **21** | Node |
| Design tokens | **74 pairs** | `tokens.css` |
| End-to-end | **11** | Real browser, full stack |
| **Total** | **234 assertions across 6 suites** | |

Phase 10 added **95** of these. The frontend had **zero** tests before it.

---

## 1. What each layer is for

A test suite is only useful if each layer catches something the others cannot.

| Layer | Catches | Cannot catch |
| --- | --- | --- |
| **Unit** | Domain rules, money arithmetic, crypto behaviour, value-object validation | Anything about wiring, SQL, or HTTP |
| **Architecture** | Module boundaries eroding, `double` near money, static clock reads | Runtime behaviour of any kind |
| **Integration** | RLS, tenant isolation, authorization, the real query plans, the real error contract | Anything the browser does |
| **Frontend unit** | Formatters, the session crypto contract, error classification | Rendering, navigation, forms |
| **Design tokens** | Contrast regressions in either theme | Whether the tokens are used |
| **E2E** | Server Actions, redirects, cookie attributes, what the browser can actually see | Fine-grained edge cases — too slow and too vague |

The rule applied throughout: **push each assertion to the cheapest layer that can
actually make it.** The E2E suite has 11 tests, not 60, because most of what it
could assert is asserted faster and more precisely below it.

---

## 2. What Phase 10 added

### 2.1 Frontend tests, from zero

- **`packages/api-client`** (14) — `formatMoney` never round-trips through a
  JavaScript number, `agingBucket` boundaries, `ApiError` classification. The
  money tests use a 16-digit value and 0.1/0.3, which a `double` corrupts and a
  string does not.
- **`apps/web`** (7) — the session cookie crypto contract: round-trip, no
  plaintext in the cookie, a different value each time, and rejection of
  tampered, wrong-key, truncated and nonsense cookies.

### 2.2 The "Configured but untested" gaps from Phase 9

The security review marked several controls as implemented-but-unasserted. All
closed:

| Control | Now asserted by |
| --- | --- |
| Argon2 rehash on parameter change | `Flags_a_hash_made_with_weaker_parameters` |
| Argon2 salting | `Salts_every_hash` |
| Argon2 minimum parameters | `Uses_at_least_the_owasp_minimum_parameters` — guards against someone lowering cost to speed up a suite |
| Refresh tokens stored hashed | `Creates_a_refresh_token_whose_stored_form_is_a_hash` |
| TOTP window is ±1 step and no wider | `Accepts_the_adjacent_steps_but_not_the_ones_beyond` |
| Envelope encryption bound to tenant + record | `Refuses_to_decrypt_under_a_different_tenant` / `_record` |
| Key length validation | `Refuses_a_key_of_the_wrong_length`, `Refuses_a_signing_key_shorter_than_the_hmac_block` |
| Challenge-token expiry with no skew grace | `Rejects_an_expired_challenge_token_with_no_skew_grace` |
| Cookie `HttpOnly` / `SameSite` | E2E `the browser never holds a token` |

### 2.3 End-to-end, closing the Phase 8 gap

Phase 8 recorded that the login form had no automated test because Next Server
Actions cannot be driven by a plain HTTP POST. That is now covered by a real
browser.

The most valuable assertion in the suite is `the browser never holds a token`:
no `eyJ…` in the HTML, nothing in `localStorage` or `sessionStorage`, the session
cookie present but `HttpOnly` and `SameSite=Lax`, its value opaque, and
`document.cookie` unable to see it. That is the entire BFF design, verified from
the attacker's side of the browser.

---

## 3. Findings

### T-1 — The web app served HTML with no security headers · **FIXED**

The API sets a full header set; `apps/web` — which actually serves markup and
runs scripts — set none. Found by writing the E2E assertion and watching it fail.

Now sets `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`,
`Permissions-Policy`, `Cross-Origin-Opener-Policy`, and a CSP with
`frame-ancestors 'none'`, `object-src 'none'`, `form-action 'self'`.

**Carried forward:** the CSP needs `'unsafe-inline'` for `style-src` because Next
inlines critical CSS. Removing it requires nonce-based styling — a real change,
not a config tweak. Recorded rather than pretended away.

### T-2 — Production rate limits make automated suites impossible · **Accepted, documented**

The auth limit (10 per 15 minutes per IP) is correct for production and hostile
to any suite signing in repeatedly from one address. Both the integration suite
and the E2E hit it.

Resolved by making limits configurable with **production values as the
defaults** — an environment that sets nothing gets production behaviour — and by
testing the limiter separately in `RateLimitingTests`, which configures a limit
of three and proves it fires. The alternative, disabling the limiter for tests,
would leave a compliance-checklist control that nothing exercises.

E2E therefore needs `RateLimits__AuthPermitsPerWindow` raised. Documented in §4.

### T-3 — Four E2E failures that were test bugs, not app bugs

Recorded because they are the failure modes of this kind of suite:

1. **The suite could only run once.** The first sign-in enrols the account; later
   runs found it enrolled and threw. Restructured around Playwright's
   `storageState`.
2. **TOTP replay.** Signing in per test consumed a code per test, which the
   single-use-per-step control correctly refused. One sign-in in a setup project
   fixed it — and notably, the *right* fix was to stop fighting the control.
3. **`dependencies` does not apply `storageState`.** It only orders projects.
   Every "authenticated" test was running signed out, failing with "element not
   found" rather than anything about authentication.
4. **The anonymous suite ran in both projects**, so an "unauthenticated visitor"
   test executed with a session.

---

## 4. Running the suites

Unit, architecture and design tokens need nothing:

```bash
dotnet test backend/MautoDesk.sln -c Release
```

```bash
pnpm --dir frontend -r test
```

Integration needs the database:

```bash
docker compose up -d postgres && docker compose run --rm migrate
```

```bash
docker exec mautodesk-postgres psql -U postgres -d mautodesk -c "alter role mautodesk_app with password 'devpw';"
```

**E2E needs the whole stack plus two pieces of configuration.** Start the API
with a raised auth limit (see T-2) and the web app, then:

```bash
pnpm --dir frontend --filter @mautodesk/e2e exec playwright test
```

The E2E account must have MFA unenrolled at the start of a run, because the suite
enrols it and captures the secret — it cannot know an existing secret. Reset with
`db/tests/reset-e2e-account.sql`.

---

## 5. What is still not tested

Stated plainly. The absence of a test is a decision, and undocumented decisions
become assumptions.

| Gap | Why, and what it would take |
| --- | --- |
| **No component tests for React components** | The primitives are pure rendering over tokens; the E2E covers whether they appear. A component-test harness earns its place when there is interactive state to drive — which arrives with the first form |
| **No performance or load testing** | The constitution asks for it and Phase 1 sets numeric budgets. Nothing measures them. k6 against a seeded dataset is the plan; it needs a deployed environment to be meaningful |
| **No accessibility automation in CI** | `axe` is specified in Phase 5. The design tokens are contrast-tested, but no test drives a rendered page through an accessibility checker |
| **No mutation testing** | 234 assertions say a lot about coverage and nothing about whether they would catch a defect. Stryker.NET would answer that |
| **No visual regression** | The design system is verified numerically (contrast) but nothing catches a layout regression |
| **Unbuilt modules** | Deals, documents, signatures, OCR, messaging and publishing have no tests because they have no code. The deal engine will need golden-file tests per jurisdiction before it can be trusted with a contract |
| **The `Observed` rows from Phase 9** | Login attempts recorded, privileged lookups minimal, TOTP secrets encrypted at rest — verified by hand, no regression guard |

---

## 6. What I would do next

1. **Mutation testing on the domain and crypto.** 234 assertions is a number;
   mutation score is evidence. The deal engine will need it.
2. **`axe` in CI on the rendered pages.** WCAG 2.2 AA is a stated commitment and
   only its colour half is currently enforced.
3. **A k6 profile**, once there is somewhere to run it against.
