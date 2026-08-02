import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';
import { apiClient } from '@/lib/api';
import { stashRecoveryCodes, takeRecoveryCodes } from '@/lib/session';
import { Note } from '@/components/primitives';

export const dynamic = 'force-dynamic';

/**
 * Recovery codes.
 *
 * Reached in two ways: straight after enrolment, carrying a freshly issued set,
 * or later from account settings to check how many are left and replace them.
 *
 * The codes are shown once and never again — they are stored hashed, so there
 * is nothing to come back to. That is why this page interrupts the sign-in flow
 * rather than waiting to be discovered: a user who never learns the codes exist
 * has no way back in when their phone goes missing, which is the exact failure
 * this feature was built to prevent.
 */
export default async function RecoveryCodesPage() {
  // Reading clears them, so a refresh does not leave credentials sitting in a
  // cookie, and a shared screen does not keep showing them.
  const issued = await takeRecoveryCodes();

  const client = await apiClient();
  const status = await client.recoveryCodeStatus();

  async function regenerate() {
    'use server';

    const authenticated = await apiClient();
    const set = await authenticated.regenerateRecoveryCodes();

    await stashRecoveryCodes(set.codes ?? []);
    revalidatePath('/settings/recovery-codes');
    redirect('/settings/recovery-codes');
  }

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-6 px-5 py-10">
      <div>
        <h1 className="t-page m-0">Recovery codes</h1>
        <p className="mt-1 text-xs text-muted">
          Use one of these instead of your authenticator if you lose your phone. Each code works
          once.
        </p>
      </div>

      {issued ? (
        <>
          <Note tone="warning" title="Save these now">
            This is the only time these codes will be shown. Print them or put them in a password
            manager — not in the same place as your phone.
          </Note>

          <ul className="grid grid-cols-2 gap-2 rounded-md border border-line bg-inset p-4">
            {issued.map((code) => (
              <li key={code} className="select-all font-mono text-sm tracking-wider text-ink">
                {code}
              </li>
            ))}
          </ul>
        </>
      ) : (
        <>
          <Note
            tone={status.remaining <= 2 ? 'danger' : 'info'}
            title={`${status.remaining} of ${status.setSize} codes remaining`}
          >
            {status.remaining <= 2
              ? 'Generate a new set now. Running out means an administrator has to get you back in.'
              : 'Generating a new set immediately invalidates every code from the old one.'}
          </Note>

          <form action={regenerate}>
            <button
              type="submit"
              className="min-h-11 rounded-md px-4 text-base font-semibold"
              style={{ background: 'var(--accent-bg)', color: 'var(--text-on-accent)' }}
            >
              Generate new codes
            </button>
          </form>
        </>
      )}
    </div>
  );
}
