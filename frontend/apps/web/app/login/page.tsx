import { redirect } from 'next/navigation';
import { anonymousClient } from '@/lib/api';
import { stashRecoveryCodes, writeSession } from '@/lib/session';
import { Note } from '@/components/primitives';

export const dynamic = 'force-dynamic';

/**
 * Sign-in.
 *
 * Three steps, because MFA is mandatory: password, then either a TOTP code or a
 * first-time authenticator enrolment. The server decides which; this page only
 * renders what it is told, so the client can never route around the second
 * factor.
 *
 * Everything runs as a Server Action, so the password and the TOTP code are
 * posted to the Next.js server and never handled by client-side JavaScript —
 * and the tokens that come back are sealed into an HttpOnly cookie without ever
 * reaching the browser.
 */
export default async function LoginPage({
  searchParams,
}: {
  readonly searchParams: Promise<Record<string, string | undefined>>;
}) {
  const params = await searchParams;
  const stage = params['stage'] ?? 'password';
  const challenge = params['challenge'];
  const secret = params['secret'];
  const error = params['error'];
  const reason = params['reason'];

  async function signIn(formData: FormData) {
    'use server';

    const email = String(formData.get('email') ?? '');
    const password = String(formData.get('password') ?? '');

    const response = await fetch(`${process.env['API_BASE_URL']}/api/v1/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
      cache: 'no-store',
    });

    if (!response.ok) {
      const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
      redirect(`/login?error=${encodeURIComponent(problem?.detail ?? 'Sign-in failed.')}`);
    }

    const body = (await response.json()) as {
      outcome: string;
      challengeToken?: string;
      enrolmentSecret?: string;
    };

    if (body.outcome === 'mfa_enrolment_required') {
      redirect(
        `/login?stage=enrol&challenge=${encodeURIComponent(body.challengeToken ?? '')}` +
          `&secret=${encodeURIComponent(body.enrolmentSecret ?? '')}`,
      );
    }

    redirect(`/login?stage=code&challenge=${encodeURIComponent(body.challengeToken ?? '')}`);
  }

  async function submitCode(formData: FormData) {
    'use server';

    const code = String(formData.get('code') ?? '');
    const challengeToken = String(formData.get('challenge') ?? '');
    const submittedStage = String(formData.get('stage') ?? 'code');
    const path =
      submittedStage === 'enrol'
        ? 'mfa/enrol'
        : submittedStage === 'recovery'
          ? 'mfa/recovery'
          : 'mfa/verify';

    const response = await fetch(`${process.env['API_BASE_URL']}/api/v1/auth/${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ challengeToken, code }),
      cache: 'no-store',
    });

    if (!response.ok) {
      const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
      redirect(
        `/login?stage=${submittedStage}&challenge=${encodeURIComponent(challengeToken)}` +
          `&error=${encodeURIComponent(problem?.detail ?? 'That code was not accepted.')}`,
      );
    }

    const body = (await response.json()) as {
      tokens: { accessToken: string; refreshToken: string; expiresIn: number };
      recoveryCodes?: readonly string[];
    };

    await writeSession({
      accessToken: body.tokens.accessToken,
      refreshToken: body.tokens.refreshToken,
      accessTokenExpiresAt: Math.floor(Date.now() / 1000) + body.tokens.expiresIn,
      email: '',
    });

    // Enrolment issues recovery codes, and this response is the only time the
    // server will ever hand them over. Divert to the page that shows them
    // instead of dropping the user straight into inventory, where they would
    // never learn the codes existed.
    if (body.recoveryCodes && body.recoveryCodes.length > 0) {
      await stashRecoveryCodes(body.recoveryCodes);
      redirect('/settings/recovery-codes?issued=1');
    }

    redirect('/inventory');
  }

  void anonymousClient;

  return (
    <div className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 px-5 py-10">
      <div>
        <h1 className="t-page m-0">Sign in to MautoDesk</h1>
        <p className="mt-1 text-xs text-muted">
          {stage === 'password'
            ? 'Use your dealership email address.'
            : stage === 'enrol'
              ? 'Set up your authenticator to finish signing in.'
              : stage === 'recovery'
                ? 'Enter one of the recovery codes you saved when you set up your authenticator.'
                : 'Enter the code from your authenticator app.'}
        </p>
      </div>

      {reason === 'expired' ? (
        <Note tone="warning" title="Your session ended">
          Sign in again to continue. If you did not sign out, this can also happen after a security
          event on your account.
        </Note>
      ) : null}

      {error ? (
        <Note tone="danger" title="Sign-in failed">
          {error}
        </Note>
      ) : null}

      {stage === 'password' ? (
        <form action={signIn} className="flex flex-col gap-4">
          <Field label="Email" name="email" type="email" autoComplete="username" required />
          <Field
            label="Password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
          />
          <SubmitButton>Continue</SubmitButton>
        </form>
      ) : (
        <form action={submitCode} className="flex flex-col gap-4">
          <input type="hidden" name="challenge" value={challenge ?? ''} />
          <input type="hidden" name="stage" value={stage} />

          {stage === 'enrol' && secret ? (
            <div className="flex flex-col gap-2 rounded-r-md border border-l-[3px] border-line bg-surface p-4"
                 style={{ borderLeftColor: 'var(--info-mark)' }}>
              <span className="t-label">Set up your authenticator</span>
              <p className="m-0 text-xs text-muted">
                Add this key to Google Authenticator, 1Password, or any TOTP app, then enter the
                six-digit code it shows.
              </p>
              <code className="select-all break-all rounded-sm bg-inset p-2 font-mono text-xs">
                {secret}
              </code>
              <p className="m-0 text-[0.6875rem] text-faint">
                Multi-factor authentication is required for every account that can see customer
                information. It cannot be turned off.
              </p>
            </div>
          ) : null}

          {stage === 'recovery' ? (
            <Field
              label="Recovery code"
              name="code"
              type="text"
              // Not numeric, and no one-time-code autofill: a recovery code is
              // letters and digits off a printout, not something an
              // authenticator can offer.
              autoComplete="off"
              autoCapitalize="characters"
              spellCheck={false}
              maxLength={16}
              placeholder="XXXXX-XXXXX"
              required
            />
          ) : (
            <Field
              label="Six-digit code"
              name="code"
              type="text"
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              required
            />
          )}

          <SubmitButton>{stage === 'enrol' ? 'Confirm and sign in' : 'Sign in'}</SubmitButton>

          {stage === 'code' ? (
            <a
              className="text-center text-xs text-muted underline"
              href={`/login?stage=recovery&challenge=${encodeURIComponent(challenge ?? '')}`}
            >
              Lost your phone? Use a recovery code
            </a>
          ) : null}

          {stage === 'recovery' ? (
            <a
              className="text-center text-xs text-muted underline"
              href={`/login?stage=code&challenge=${encodeURIComponent(challenge ?? '')}`}
            >
              Back to your authenticator code
            </a>
          ) : null}
        </form>
      )}
    </div>
  );
}

function Field({
  label,
  name,
  ...rest
}: { readonly label: string; readonly name: string } & React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="flex flex-col gap-1">
      <span className="t-label">{label}</span>
      <input
        name={name}
        // 16px minimum: below it, iOS zooms the viewport on focus and the form
        // jumps under the user's thumb.
        className="min-h-11 rounded-md border border-control bg-surface px-3 text-base text-ink"
        {...rest}
      />
    </label>
  );
}

function SubmitButton({ children }: { readonly children: React.ReactNode }) {
  return (
    <button
      type="submit"
      className="min-h-11 rounded-md px-4 text-base font-semibold"
      style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
    >
      {children}
    </button>
  );
}
