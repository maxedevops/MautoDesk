import 'server-only';

import { redirect } from 'next/navigation';
import { ApiError, MautoDeskClient } from '@mautodesk/api-client';
import { clearSession, readSession, writeSession } from './session';

/**
 * The API client for the current request, carrying the signed-in user's token.
 *
 * **Server-only, deliberately.** The `server-only` import turns "never call the
 * API from the browser" into a build error rather than a code-review comment.
 * The browser holds a sealed HttpOnly cookie and no credential of its own.
 *
 * Refreshes proactively when the access token is close to expiry, so a user
 * mid-task does not hit a 401 and get bounced to the login screen for no
 * visible reason.
 */
export async function apiClient(): Promise<MautoDeskClient> {
  const baseUrl = process.env['API_BASE_URL'] ?? 'http://localhost:5080';
  const session = await readSession();

  if (!session) {
    redirect('/login');
  }

  const accessToken = await ensureFreshToken(session.accessToken, session, baseUrl);

  return new MautoDeskClient({
    baseUrl,
    headers: { Authorization: `Bearer ${accessToken}` },
  });
}

/** An unauthenticated client, for the login flow itself. */
export function anonymousClient(): MautoDeskClient {
  return new MautoDeskClient({
    baseUrl: process.env['API_BASE_URL'] ?? 'http://localhost:5080',
  });
}

/** The permissions in the current token, for deciding what to render. */
export async function currentPermissions(): Promise<ReadonlySet<string>> {
  const session = await readSession();
  if (!session) return new Set();

  // Read from the token itself rather than a second API call, so what the UI
  // hides is exactly what the server will enforce on the next request. Decoding
  // is not verification — the server verifies; this only reads a claim we
  // already possess.
  const payload = session.accessToken.split('.')[1];
  if (!payload) return new Set();

  try {
    const claims = JSON.parse(Buffer.from(payload, 'base64url').toString('utf8')) as {
      perm?: string | string[];
    };

    const perms = claims.perm;
    if (!perms) return new Set();

    return new Set(Array.isArray(perms) ? perms : [perms]);
  } catch {
    return new Set();
  }
}

export async function currentEmail(): Promise<string | null> {
  return (await readSession())?.email ?? null;
}

/**
 * Refreshes when the access token is nearly expired.
 *
 * @remarks
 * A 30-second margin: long enough that a request in flight will not expire
 * mid-journey, short enough that we are not rotating tokens constantly. On
 * failure the session is cleared and the user is sent to sign in again — a
 * refresh failure means the family was revoked, which is exactly what should
 * happen after a token reuse is detected.
 */
async function ensureFreshToken(
  accessToken: string,
  session: { refreshToken: string; accessTokenExpiresAt: number; email: string },
  baseUrl: string,
): Promise<string> {
  const secondsRemaining = session.accessTokenExpiresAt - Math.floor(Date.now() / 1000);

  if (secondsRemaining > 30) {
    return accessToken;
  }

  try {
    const response = await fetch(`${baseUrl}/api/v1/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: session.refreshToken }),
      cache: 'no-store',
    });

    if (!response.ok) {
      throw new ApiError(response.status);
    }

    const tokens = (await response.json()) as {
      accessToken: string;
      refreshToken: string;
      expiresIn: number;
    };

    await writeSession({
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + tokens.expiresIn,
      email: session.email,
    });

    return tokens.accessToken;
  } catch {
    await clearSession();
    redirect('/login?reason=expired');
  }
}
