import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end configuration.
 *
 * Deliberately does NOT start the stack. E2E needs PostgreSQL, the migrated
 * schema, the API, and the web app running together, which is a `docker compose`
 * plus two processes — orchestrating that from a test runner produces a fixture
 * that works on one machine and nowhere else. The README documents the three
 * commands; CI runs them as explicit steps.
 *
 * Excluded from `pnpm -r test` for the same reason: a suite that fails because
 * the developer did not happen to have a database running is a suite people
 * learn to ignore.
 */
export default defineConfig({
  testDir: '.',
  testMatch: '**/*.e2e.ts',

  // Serial. These tests sign in, which mutates account state — MFA enrolment,
  // failed-attempt counters, lockout. Running them in parallel makes them race
  // over the same rows.
  fullyParallel: false,
  workers: 1,

  // No retries locally: a flaky E2E should be fixed, not masked. One retry in
  // CI absorbs genuine infrastructure flakiness without hiding a real failure,
  // because a test that only passes on retry still shows up in the report.
  retries: process.env['CI'] ? 1 : 0,

  reporter: process.env['CI'] ? [['github'], ['list']] : [['list']],

  timeout: 30_000,
  expect: { timeout: 10_000 },

  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:3000',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },

  projects: [
    // Signs in once and saves the sealed cookie. Every authenticated test then
    // starts already signed in, so the suite consumes exactly one TOTP code
    // rather than one per test — which the single-use-per-step control would
    // otherwise (correctly) refuse.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },

    {
      name: 'chromium',
      // The anonymous suite has its own project with an empty cookie jar. Without
      // this exclusion it ALSO runs here, signed in — and a test asserting "an
      // unauthenticated visitor is redirected" passes or fails for reasons that
      // have nothing to do with the behaviour it names.
      testIgnore: /anonymous\.e2e\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        // `dependencies` only ORDERS the setup project; it does not apply what
        // the setup saved. Without this the "authenticated" tests run signed
        // out and fail in confusing ways — every page redirects to /login and
        // the assertions report missing elements rather than missing auth.
        storageState: 'playwright/.auth/session.json',
      },
      dependencies: ['setup'],
    },

    // The unauthenticated cases must NOT inherit the saved session.
    {
      name: 'anonymous',
      testMatch: /anonymous\.e2e\.ts/,
      use: { ...devices['Desktop Chrome'], storageState: { cookies: [], origins: [] } },
    },
  ],
});
