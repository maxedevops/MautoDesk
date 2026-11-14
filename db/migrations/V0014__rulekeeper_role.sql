--------------------------------------------------------------------------------
-- MautoDesk — V0014 the rule-keeper identity
--
-- WHY THIS EXISTS
--
-- Approving a regulatory rule was impossible. V0010 grants mautodesk_app SELECT
-- and nothing else on the law tables, deliberately — "a compromised application
-- cannot rewrite the law it is evaluated against" — and
-- compliance.regulatory_rule_version carries app.deny_mutation() on UPDATE and
-- DELETE, so an existing row's approved_at can never be set by anyone. V0012's
-- header told a reviewer to "set status, approved_at and approved_by", which no
-- role in this database could do.
--
-- The design was right and only the procedure was wrong: an approval is a NEW
-- version row, inserted already carrying approved_at and approved_by, which the
-- exclusion constraint then holds to non-overlapping effective dates. What was
-- missing was somebody able to perform that insert.
--
-- WHY A SEPARATE ROLE RATHER THAN AN API ENDPOINT
--
-- Same reasoning as mautodesk_dispatcher in V0011. Granting the insert to
-- mautodesk_app would hand every request handler in the API — and anything that
-- compromises one — the ability to write the law the deal is judged against.
-- The reviewer's signature is the most security-sensitive act in this product,
-- and it does not belong on the same credential that serves HTTP.
--
-- So mautodesk_rulekeeper can do exactly two things: INSERT a regulatory rule
-- version, and APPEND a platform-level audit event saying it did. It cannot
-- update or delete anything, anywhere. It cannot see a vehicle, a customer, a
-- deal or a tenant. It is used by MautoDesk.Compliance.RuleKeeper, an operator
-- tool that is not reachable over the network.
--
-- WHAT THIS ROLE STILL CANNOT DO, ON PURPOSE
--
-- It cannot author a rule, a source, or a test — only approve a version that
-- already exists as a draft in a migration. Composing the law stays a reviewed
-- change to db/migrations; approving it is an operator action against a draft a
-- human has read. Those are different acts by different people and they keep
-- different credentials.
--------------------------------------------------------------------------------

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'mautodesk_rulekeeper') then
    -- No password here. Set per environment, exactly as the other two roles are,
    -- so a migration file never contains a credential.
    create role mautodesk_rulekeeper login;
  end if;
end
$$;

comment on role mautodesk_rulekeeper is
  'Inserts approved regulatory rule versions on a named reviewer''s written '
  'sign-off, and appends the platform audit event recording it. Deliberately has '
  'no BYPASSRLS, owns nothing, holds no UPDATE or DELETE on any table, and can '
  'read nothing outside the compliance law tables. If this role is compromised '
  'the blast radius is a rule version that the overlap constraint, the approval '
  'gate and the audit chain all describe — not the customer database.';

grant usage on schema compliance, audit to mautodesk_rulekeeper;

--------------------------------------------------------------------------------
-- Reading. The tool validates before it writes, and it cannot validate what it
-- cannot see: the draft, its rule, its archived source, and its test corpus.
--------------------------------------------------------------------------------
grant select on
  compliance.regulatory_jurisdiction,
  compliance.regulatory_authority,
  compliance.regulatory_source,
  compliance.regulatory_source_version,
  compliance.regulatory_rule,
  compliance.regulatory_rule_version,
  compliance.regulatory_rule_test,
  compliance.jurisdiction_holiday
  to mautodesk_rulekeeper;

--------------------------------------------------------------------------------
-- Writing. One table, one verb.
--------------------------------------------------------------------------------
grant insert on compliance.regulatory_rule_version to mautodesk_rulekeeper;

--------------------------------------------------------------------------------
-- The audit entry.
--
-- audit.event is FORCE row level security with a policy scoped to
-- mautodesk_app, so a different role matches no policy and is refused
-- everything. The rule-keeper needs its own, and it is written as narrowly as
-- the act it records: platform scope only (tenant_id is null, because approving
-- Oklahoma law is not one dealership's event), and only this one action.
--
-- INSERT is not enough on its own, and the reason is worth writing down because
-- it is a silent failure rather than a loud one. audit.chain_event() runs BEFORE
-- INSERT as the CALLING role and reads the previous row to compute prev_hash:
--
--     select e.hash into v_prev from audit.event e
--      where e.tenant_id is not distinct from new.tenant_id
--      order by e.id desc limit 1;
--
-- A role that may insert but cannot SELECT the ledger does not get an error from
-- that query. It gets zero rows, so v_prev is NULL, so the new row claims to be
-- the first in the chain — and audit.verify_chain() then reports the chain broken
-- at every such row. An append-only ledger whose links are quietly absent is
-- worse than no ledger, because it still looks like evidence.
--
-- So the rule-keeper gets a SELECT policy too, scoped to platform-scope rows
-- (tenant_id is null). Those rows are, by definition, not any dealership's data:
-- they are events like this one. It still cannot see a single tenant's audit
-- trail, and it still cannot update or delete anything.
--------------------------------------------------------------------------------
grant insert, select on audit.event to mautodesk_rulekeeper;

drop policy if exists event_rulekeeper_append on audit.event;
drop policy if exists event_rulekeeper_chain  on audit.event;

create policy event_rulekeeper_append on audit.event
  for insert to mautodesk_rulekeeper
  with check (
    tenant_id is null
    and actor_type = 'platform_admin'
    and action = 'compliance.rule_version.approved'
    -- V0001 says access_reason is mandatory for platform_admin and that the
    -- application checks it. For this path the database checks it.
    and access_reason is not null
    and btrim(access_reason) <> ''
  );

create policy event_rulekeeper_chain on audit.event
  for select to mautodesk_rulekeeper
  using (tenant_id is null);

comment on policy event_rulekeeper_append on audit.event is
  'The rule-keeper may append exactly one kind of platform-scope event, and must '
  'name a reason. Anything else it tries to write fails the WITH CHECK rather '
  'than landing in the one ledger that exists to be trusted.';

comment on policy event_rulekeeper_chain on audit.event is
  'Read access exists so audit.chain_event() can find the previous platform-scope '
  'row and link to it. Without it the trigger silently writes prev_hash = NULL '
  'and the chain forks. Scoped to tenant_id is null: no dealership''s audit trail '
  'is visible to this role.';

--------------------------------------------------------------------------------
-- NOT fixed here, and recorded rather than quietly widened: mautodesk_app has
-- the same problem for platform-scope rows. Its policy is
-- tenant_id = app.current_tenant_id(), which is never true when tenant_id is
-- null, so any platform-scope event the API writes also gets prev_hash = NULL.
-- The fix is a policy decision — V0006 says platform rows are "deliberately NOT
-- visible to a tenant session" — and reversing that is not a migration's call to
-- make on its own. See docs/17-approval-path.md.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Belt and braces: no sequence privileges, nothing in the tenant schemas, and
-- no inherited rights from a group. Asserted by db/tests/rulekeeper_grants.sql
-- rather than assumed, because a grant nobody checks is a grant that drifts.
--------------------------------------------------------------------------------
revoke all on all tables in schema platform, identity, inventory, crm, sales,
  documents, signatures, messaging, publishing, billing from mautodesk_rulekeeper;

do $$
declare
  can_bypass boolean;
begin
  select rolsuper or rolbypassrls into can_bypass
    from pg_roles where rolname = 'mautodesk_rulekeeper';

  if can_bypass then
    raise exception 'mautodesk_rulekeeper must not be able to bypass row level security';
  end if;

  raise notice
    'mautodesk_rulekeeper created: select on 8 law tables, insert on '
    'regulatory_rule_version and audit.event, no update or delete anywhere.';
end
$$;
