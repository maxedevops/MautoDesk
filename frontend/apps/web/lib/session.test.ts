import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { beforeAll, describe, expect, it } from 'vitest';

/**
 * The BFF session cookie.
 *
 * The seal/unseal pair is reimplemented here rather than imported, because
 * `lib/session.ts` imports `server-only` and calls `next/headers` — neither of
 * which exists outside a request. What is under test is the *crypto contract*:
 * that a sealed cookie round-trips, and that a tampered or wrong-key cookie is
 * rejected rather than partially trusted.
 *
 * The duplication is deliberate and small; if it drifts, the E2E login test
 * catches it, because that exercises the real implementation end to end.
 */

const NONCE_BYTES = 12;
const TAG_BYTES = 16;

let key: Buffer;

beforeAll(() => {
  key = randomBytes(32);
});

interface SessionData {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: number;
  email: string;
}

function seal(data: SessionData, withKey: Buffer = key): string {
  const nonce = randomBytes(NONCE_BYTES);
  const cipher = createCipheriv('aes-256-gcm', withKey, nonce);
  const body = Buffer.concat([cipher.update(JSON.stringify(data), 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, cipher.getAuthTag(), body]).toString('base64url');
}

function unseal(sealed: string, withKey: Buffer = key): SessionData | null {
  try {
    const raw = Buffer.from(sealed, 'base64url');
    if (raw.length < NONCE_BYTES + TAG_BYTES) return null;

    const decipher = createDecipheriv('aes-256-gcm', withKey, raw.subarray(0, NONCE_BYTES));
    decipher.setAuthTag(raw.subarray(NONCE_BYTES, NONCE_BYTES + TAG_BYTES));

    const plain = Buffer.concat([
      decipher.update(raw.subarray(NONCE_BYTES + TAG_BYTES)),
      decipher.final(),
    ]);

    return JSON.parse(plain.toString('utf8')) as SessionData;
  } catch {
    return null;
  }
}

const session: SessionData = {
  accessToken: 'eyJhbGciOiJIUzI1NiJ9.payload.signature',
  refreshToken: 'opaque-refresh-token-value',
  accessTokenExpiresAt: 1_800_000_000,
  email: 'dana@ridgeline.test',
};

describe('session cookie', () => {
  it('round-trips the tokens', () => {
    expect(unseal(seal(session))).toEqual(session);
  });

  /**
   * The reason the cookie is encrypted rather than merely signed.
   *
   * Signing prevents tampering but leaves the tokens readable by anything that
   * can read the cookie jar — a browser extension, a shared machine, a backup.
   */
  it('does not expose the tokens in the cookie value', () => {
    const sealed = seal(session);

    expect(sealed).not.toContain(session.accessToken);
    expect(sealed).not.toContain(session.refreshToken);
    expect(sealed).not.toContain('eyJ');
    expect(sealed).not.toContain(session.email);
  });

  it('produces a different value each time, so the cookie is not a fingerprint', () => {
    // A deterministic seal would let an observer tell two users apart, or spot
    // that a user's session had not changed, purely from the cookie value.
    expect(seal(session)).not.toBe(seal(session));
  });

  it('rejects a tampered cookie rather than returning partial data', () => {
    const sealed = seal(session);
    const raw = Buffer.from(sealed, 'base64url');

    // Flip one bit in the ciphertext body.
    raw[raw.length - 1] ^= 0x01;

    expect(unseal(raw.toString('base64url'))).toBeNull();
  });

  it('rejects a cookie sealed with a different key', () => {
    // What an attacker holds after stealing a cookie from another deployment,
    // or what every user holds after a key rotation.
    const sealed = seal(session, randomBytes(32));

    expect(unseal(sealed)).toBeNull();
  });

  it('rejects a truncated cookie', () => {
    const sealed = seal(session);

    expect(unseal(sealed.slice(0, 10))).toBeNull();
    expect(unseal('')).toBeNull();
  });

  it('rejects a value that is not a cookie at all', () => {
    // A tampered, stale, or nonsense cookie must read as "not signed in", never
    // as an error the user sees.
    expect(unseal('not-base64url-at-all!!!')).toBeNull();
    expect(unseal('YWJjZGVm')).toBeNull();
  });
});
