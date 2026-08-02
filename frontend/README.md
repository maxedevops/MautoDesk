# Frontend — Next.js

**Status: not yet implemented.** Phase 7. Phase 5 (UI/UX) comes first.

## Intended layout

```
apps/
  web/               The dealer application. App Router, and the BFF that holds tokens
  site/              (Release 2) public inventory site + embeddable widget
packages/
  api-client/        GENERATED from contracts/openapi.yaml — never hand-edited
  ui/                Design system: primitives, tokens, dark mode, a11y-tested
  config/            Shared eslint, tsconfig, tailwind preset
```

## Non-negotiables

- **The browser never holds a JWT.** `apps/web` is a backend-for-frontend: the browser gets an
  `HttpOnly; Secure; SameSite=Lax` session cookie, the Next.js server holds the access and refresh
  tokens and attaches the bearer token when calling the API. This is what makes XSS unable to
  exfiltrate a token.
- **`packages/api-client` is generated.** Editing it by hand defeats ADR-0010, whose whole point is
  that a front-end/back-end type mismatch is a red build rather than a runtime `undefined`.
- **React Query owns server state. Zustand owns only UI state** — command palette, sidebar, active
  filters. Server data in a global store is how two components end up disagreeing about what a
  vehicle costs.
- **Permissions hide UI; they do not enforce anything.** `/auth/me` returns effective permissions so
  the UI can hide what a user cannot do. The server enforces regardless — a hidden button is not an
  access control.
- **Money is a string end to end.** Never parse an amount into a JavaScript number. Format for
  display; compute nowhere.

## Mobile is not a smaller desktop

Recon, VIN scan, photo capture, and lead response are **mobile-first** with dedicated layouts —
these happen on a lot, on a phone, with a customer waiting. Deals, accounting, and reporting are
desktop-first. Squeezing a desktop table onto a phone is how a DMS gets abandoned for paper.

## Enforced in CI

- Lighthouse CI budget: LCP < 2.0 s, INP < 200 ms on 4G / mid-tier Android. A regression fails the
  build.
- `axe` accessibility checks, plus a manual keyboard pass per module. Target is WCAG 2.2 AA.
