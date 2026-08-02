import 'server-only';

import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { cookies } from 'next/headers';

/**
 * The backend-for-frontend session.
 *
 * **The browser never receives a token.** It gets one opaque, sealed,
 * `HttpOnly` cookie; the access and refresh tokens live inside it, readable only
 * by the Next.js server. That is the whole point of the BFF in ADR §4: if there
 * is no credential in the browser, XSS has nothing to exfiltrate.
 *
 * The cookie is sealed with AES-256-GCM rather than merely signed. Signing would
 * stop tampering but leave the tokens readable by anything that can read the
 * cookie jar — a browser extension, a shared machine, a leaked backup.
 * Encrypting means the cookie is inert without the server key.
 */

const COOKIE_NAME = 'md_session';
const NONCE_BYTES = 12;
const TAG_BYTES = 16;

export interface SessionData {
  readonly accessToken: string;
  readonly refreshToken: string;
  /** Unix seconds. Used to refresh slightly early rather than on failure. */
  readonly accessTokenExpiresAt: number;
  readonly email: string;
}

function key(): Buffer {
  const configured = process.env['SESSION_SECRET'];

  if (!configured) {
    // Refusing beats generating a key per process: that would appear to work,
    // then log everyone out on restart and behave differently on each instance
    // behind a load balancer.
    throw new Error(
      'SESSION_SECRET is not set. Generate one with `openssl rand -base64 32`. ' +
        'Refusing to start a session store without it.',
    );
  }

  const bytes = Buffer.from(configured, 'base64');

  if (bytes.length !== 32) {
    throw new Error(
      `SESSION_SECRET must decode to exactly 32 bytes for AES-256; got ${bytes.length}.`,
    );
  }

  return bytes;
}

function seal(data: SessionData): string {
  const nonce = randomBytes(NONCE_BYTES);
  const cipher = createCipheriv('aes-256-gcm', key(), nonce);
  const body = Buffer.concat([cipher.update(JSON.stringify(data), 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, cipher.getAuthTag(), body]).toString('base64url');
}

function unseal(sealed: string): SessionData | null {
  try {
    const raw = Buffer.from(sealed, 'base64url');

    if (raw.length < NONCE_BYTES + TAG_BYTES) return null;

    const decipher = createDecipheriv('aes-256-gcm', key(), raw.subarray(0, NONCE_BYTES));
    decipher.setAuthTag(raw.subarray(NONCE_BYTES, NONCE_BYTES + TAG_BYTES));

    const plain = Buffer.concat([
      decipher.update(raw.subarray(NONCE_BYTES + TAG_BYTES)),
      decipher.final(),
    ]);

    return JSON.parse(plain.toString('utf8')) as SessionData;
  } catch {
    // A tampered, truncated, or stale-key cookie is simply not a session. It is
    // never an error the user should see — they just get the login page.
    return null;
  }
}

export async function readSession(): Promise<SessionData | null> {
  const store = await cookies();
  const raw = store.get(COOKIE_NAME)?.value;
  return raw ? unseal(raw) : null;
}

export async function writeSession(data: SessionData): Promise<void> {
  const store = await cookies();

  store.set(COOKIE_NAME, seal(data), {
    httpOnly: true,
    // Lax, not Strict: Strict would drop the cookie when a dealer follows a link
    // from their email into the app, which reads as "it logged me out again".
    // Lax still blocks the cross-site POST that CSRF depends on.
    sameSite: 'lax',
    secure: process.env.NODE_ENV === 'production',
    path: '/',
    maxAge: 60 * 60 * 24 * 30,
  });
}

export async function clearSession(): Promise<void> {
  const store = await cookies();
  store.delete(COOKIE_NAME);
}
