import { expect, test } from '@playwright/test';

/**
 * The unauthenticated surface.
 *
 * Runs in the <c>anonymous</c> project, which explicitly starts with an empty
 * cookie jar — these assertions are worthless if the saved session leaks in.
 */

const EMAIL = process.env['E2E_EMAIL'] ?? 'dana@ridgeline.test';
const PASSWORD = process.env['E2E_PASSWORD'] ?? 'Ridgeline!Demo2026';

test.describe('unauthenticated', () => {
  test('a protected page redirects to sign-in', async ({ page }) => {
    await page.goto('/inventory');

    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('heading', { name: 'Sign in to MautoDesk' })).toBeVisible();
  });

  test('the login page leaks no token material', async ({ page }) => {
    await page.goto('/login');

    expect(await page.content()).not.toMatch(/eyJ[A-Za-z0-9_-]{20,}/);
  });

  /**
   * A correct password alone is a step, never an outcome.
   *
   * The browser-level counterpart to the API test of the same name: this proves
   * the <em>form</em> cannot be walked past the second factor, not merely that
   * the endpoint refuses to.
   */
  test('a correct password alone does not sign you in', async ({ page }) => {
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page).not.toHaveURL('/inventory');
    await expect(page.getByLabel('Six-digit code')).toBeVisible();
  });

  /**
   * A wrong password and an unknown address are indistinguishable in the UI.
   * </summary>
   */
  test('a failed sign-in does not reveal whether the account exists', async ({ page }) => {
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password').fill('definitely-not-the-password');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByText('Sign-in failed')).toBeVisible();
    const known = await page.getByText('Sign-in failed').locator('..').innerText();

    await page.goto('/login');
    await page.getByLabel('Email').fill('nobody-at-all@nowhere.test');
    await page.getByLabel('Password').fill('definitely-not-the-password');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByText('Sign-in failed')).toBeVisible();
    const unknown = await page.getByText('Sign-in failed').locator('..').innerText();

    expect(unknown).toBe(known);
  });

  test('an invalid code is refused with a readable message', async ({ page }) => {
    await page.goto('/login');
    await page.getByLabel('Email').fill(EMAIL);
    await page.getByLabel('Password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Continue' }).click();

    await page.getByLabel('Six-digit code').fill('000000');
    await page.getByRole('button', { name: /Sign in|Confirm and sign in/ }).click();

    await expect(page.getByText('Sign-in failed')).toBeVisible();
    await expect(page).not.toHaveURL('/inventory');
  });
});
