import { expect, test } from '@playwright/test';

/**
 * The authenticated application, driven through a real browser.
 *
 * Closes the gap recorded in docs/08-authentication.md §8: the sign-in flow is
 * built from Next Server Actions, which cannot be driven by a plain HTTP POST —
 * they use Next's own protocol. The API flow and the session layer were both
 * covered by unit and integration tests; the form plumbing between them was not,
 * and "verified by hand" is not a regression guard.
 *
 * These tests start already signed in: <c>auth.setup.ts</c> performs the real
 * sign-in once and saves the sealed cookie. See the config for why.
 *
 * Requires the full stack — see docs/10-testing.md §4.
 */

test.describe('signed in', () => {
  test('the inventory grid renders real data', async ({ page }) => {
    await page.goto('/inventory');

    await expect(page.getByRole('heading', { name: 'Inventory' })).toBeVisible();

    // Money formatted from a decimal string, never parsed into a number.
    await expect(page.getByText(/\$\d{1,3},\d{3}\.\d{2}/).first()).toBeVisible();
  });

  /**
   * The property the whole BFF design exists for.
   *
   * If a JWT appears in the HTML, in web storage, or in a script-readable
   * cookie, XSS has something to steal and the design has failed.
   */
  test('the browser never holds a token', async ({ page, context }) => {
    await page.goto('/inventory');
    await expect(page.getByRole('heading', { name: 'Inventory' })).toBeVisible();

    expect(await page.content()).not.toMatch(/eyJ[A-Za-z0-9_-]{20,}/);

    const storage = await page.evaluate(() => ({
      local: JSON.stringify(window.localStorage),
      session: JSON.stringify(window.sessionStorage),
    }));

    expect(storage.local).not.toContain('eyJ');
    expect(storage.session).not.toContain('eyJ');

    const cookies = await context.cookies();
    const session = cookies.find((cookie) => cookie.name === 'md_session');

    expect(session, 'the session cookie must be set').toBeDefined();
    expect(session!.httpOnly, 'HttpOnly is what puts it beyond reach of scripts').toBe(true);
    expect(session!.sameSite).toBe('Lax');
    expect(session!.value, 'the cookie is encrypted, not merely signed').not.toContain('eyJ');

    // And scripts cannot see it at all.
    expect(await page.evaluate(() => document.cookie)).not.toContain('md_session');
  });

  test('a vehicle detail page opens from the grid', async ({ page }) => {
    await page.goto('/inventory');

    await page.locator('table a').first().click();

    await expect(page).toHaveURL(/\/inventory\/[0-9a-f-]{36}/);
    await expect(page.getByText('Details')).toBeVisible();
  });

  /**
   * A cross-tenant identifier is indistinguishable from a missing one.
   *
   * The browser-level counterpart to the API assertion: the user sees an
   * ordinary not-found page, with nothing confirming the record exists.
   */
  test('an unknown vehicle id shows not-found, revealing nothing', async ({ page }) => {
    const response = await page.goto('/inventory/11111111-1111-4111-8111-000000000001');

    expect(response?.status()).toBe(404);
    expect(await page.content()).not.toContain('Ridgeline');
  });

  test('security headers are present on a rendered page', async ({ page }) => {
    const response = await page.goto('/inventory');
    const headers = response!.headers();

    // This app had none until the Phase 10 E2E suite asked for them.
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['x-frame-options']).toBe('DENY');
    expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
    expect(headers['content-security-policy']).toContain("object-src 'none'");
    expect(headers['x-powered-by'], 'the framework banner is noise for an attacker').toBeUndefined();
  });
});
