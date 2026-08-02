--------------------------------------------------------------------------------
-- Resets the end-to-end test account.
--
-- The E2E suite enrols MFA and captures the secret from the enrolment screen —
-- it cannot know a secret that already exists. So the account must start each
-- run unenrolled, and with its lockout counters clear in case a previous run
-- exercised the failed-attempt path.
--
--   docker cp db/tests/reset-e2e-account.sql mautodesk-postgres:/reset.sql
--   docker exec mautodesk-postgres psql -U postgres -d mautodesk -f /reset.sql
--------------------------------------------------------------------------------

delete from identity.mfa_factor
 where user_id in (select id from identity."user" where email = 'dana@ridgeline.test');

update identity."user"
   set mfa_enrolled_at    = null,
       failed_login_count = 0,
       lockout_count      = 0,
       locked_until       = null
 where email = 'dana@ridgeline.test';

-- Sessions from a previous run are not reused, but leaving them accumulates rows
-- and makes "where am I signed in" noisy during manual testing.
delete from identity.refresh_token
 where session_id in (
   select s.id from identity.session s
     join identity."user" u on u.id = s.user_id
    where u.email = 'dana@ridgeline.test');

delete from identity.session
 where user_id in (select id from identity."user" where email = 'dana@ridgeline.test');
