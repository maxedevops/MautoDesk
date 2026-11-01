import { createHmac } from 'node:crypto';
import { expect, test as setup } from '@playwright/test';

/**
 * Signs in once and saves the session for every test that needs one.
 *
 * <b>Why a setup project rather than signing in per test.</b> Each sign-in
 * consumes a TOTP code, and codes are deliberately single-use within their
 * 30-second step — a control we built on purpose. Tests that each sign in
 * therefore have to wait out the step, which is slow, or replay a code, which
 * the server correctly refuses. Signing in once and reusing the sealed cookie
 * sidesteps both without weakening anything: the sign-in flow is still exercised
 * end to end, once, here.
 */

const EMAIL = process.env['E2E_EMAIL'] ?? 'dana@ridgeline.test';
const PASSWORD = process.env['E2E_PASSWORD'] ?? 'Ridgeline!Demo2026';

export const SESSION_FILE = 'playwright/.auth/session.json';

function base32Decode(input: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0;
  let value = 0;
  const out: number[] = [];

  for (const char of input.replace(/=+$/, '').toUpperCase()) {
    const index = alphabet.indexOf(char);
    if (index === -1) continue;
    value = (value << 5) | index;
    bits += 5;
    if (bits >= 8) {
      out.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }

  return Buffer.from(out);
}

function totp(secret: string): string {
  const counter = Math.floor(Date.now() / 1000 / 30);
  const buf = Buffer.alloc(8);
  buf.writeBigUInt64BE(BigInt(counter));

  const hmac = createHmac('sha1', base32Decode(secret)).update(buf).digest();
  const offset = hmac[hmac.length - 1]! & 0x0f;
  const code =
    ((hmac[offset]! & 0x7f) << 24) |
    ((hmac[offset + 1]! & 0xff) << 16) |
    ((hmac[offset + 2]! & 0xff) << 8) |
    (hmac[offset + 3]! & 0xff);

  return String(code % 1_000_000).padStart(6, '0');
}

setup('sign in', async ({ page }) => {
  await page.goto('/login');

  await page.getByLabel('Email').fill(EMAIL);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Continue' }).click();

  // MFA is mandatory: a correct password never lands on the grid.
  await expect(page).toHaveURL(/\/login\?stage=(code|enrol)/);

  if (page.url().includes('stage=enrol')) {
    const secret = (await page.locator('code').innerText()).trim();
    await page.getByLabel('Six-digit code').fill(totp(secret));
    await page.getByRole('button', { name: 'Confirm and sign in' }).click();
  } else {
    throw new Error(
      'The E2E account is already enrolled, so its TOTP secret is unknown. Reset MFA before ' +
        'running: see docs/10-testing.md §4.',
    );
  }

  // Enrolment does not land on the grid: it diverts to the recovery codes,
  // because that response is the only time the server hands them over.
  //
  // This asserted /inventory and had therefore never passed; the suite had
  // simply never run in CI. Once it did, the page turned out to serve an error
  // boundary — it cleared the stash cookie during render, which Next refuses —
  // so a real user saw a server error instead of their codes. The codes are now
  // read-only here and cleared by the acknowledgement, which is also what makes
  // this setup deterministic: it leaves the page by the route a user takes.
  await expect(page).toHaveURL('/settings/recovery-codes?issued=1');
  await expect(page.getByRole('heading', { name: 'Recovery codes' })).toBeVisible();
  await expect(page.locator('li')).not.toHaveCount(0);

  await page.getByRole('button', { name: 'I have saved these codes' }).click();
  await expect(page).toHaveURL('/inventory');

  // The sealed cookie, reused by every authenticated test.
  await page.context().storageState({ path: SESSION_FILE });
});
