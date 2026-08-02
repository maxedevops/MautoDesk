--------------------------------------------------------------------------------
-- MautoDesk — V0009 Refresh-token tenant lookup
--
-- The same shape of problem V0008 solved for login, in the one other place it
-- occurs: /auth/refresh and /auth/logout are anonymous by necessity. The caller
-- presents an opaque refresh token and nothing else — no tenant, no bearer
-- token — so row-level security denies the lookup and the token can never be
-- found.
--
-- The fix is the same, and deliberately just as narrow: a SECURITY DEFINER
-- function that maps a token HASH to its tenant, and returns nothing else.
--
--   * it takes a sha256 hash, never a token — the plaintext never touches the
--     database, here or anywhere,
--   * it returns exactly one column: the tenant id,
--   * it reveals nothing about whether the token is expired, rotated, or
--     revoked; those decisions stay in the tenant-scoped path afterwards, where
--     the ordinary policies apply,
--   * search_path is pinned, EXECUTE is granted only to the application role.
--
-- An attacker who could call it with a guessed hash would learn only that some
-- tenant owns it, which they cannot act on without the token itself.
--------------------------------------------------------------------------------

create or replace function identity.find_refresh_token_tenant(p_hash bytea)
returns uuid
language sql
stable
security definer
set search_path = identity, pg_temp
as $$
  select t.tenant_id
    from identity.refresh_token t
   where t.token_hash = p_hash
   limit 1;
$$;

revoke all on function identity.find_refresh_token_tenant(bytea) from public;
grant execute on function identity.find_refresh_token_tenant(bytea) to mautodesk_app;

comment on function identity.find_refresh_token_tenant(bytea) is
  'SECURITY DEFINER. Maps a refresh-token hash to its tenant so the anonymous '
  'refresh and logout endpoints can establish a scope. Returns the tenant id and '
  'nothing else; validity is decided afterwards under normal RLS.';
