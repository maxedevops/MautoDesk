--------------------------------------------------------------------------------
-- MautoDesk — V0006 Row-Level Security and grants
--
-- This migration is the implementation of ADR-0002. It is the difference
-- between "we filter by tenant" and "the database cannot return another
-- tenant's row."
--
-- The policies are applied by a loop over every table carrying a tenant_id, so
-- a new table added in a later migration cannot be forgotten as long as it
-- follows the column convention — and if it does not, app.rls_coverage_gaps()
-- reports it and the security test suite fails the build.
--
-- Reference tables (shared, non-tenant data) are listed explicitly. Adding a
-- table to that list is a deliberate act that shows up in code review, which is
-- exactly the friction we want on a decision to make data cross-tenant.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Reference tables: shared across tenants by design, no RLS, read-only to the
-- application. Every entry needs a one-line justification.
--------------------------------------------------------------------------------
create table app.rls_exempt_table (
  schema_name text not null,
  table_name  text not null,
  reason      text not null,
  primary key (schema_name, table_name)
);

insert into app.rls_exempt_table (schema_name, table_name, reason) values
  ('inventory','vin_decode_cache',   'Public VIN reference data; contains no customer information'),
  ('sales','tax_jurisdiction',       'Public jurisdiction reference data'),
  ('sales','postal_jurisdiction',    'Public postal-to-jurisdiction mapping'),
  ('sales','rule_set',               'Published tax/fee rules; source-cited public law'),
  ('identity','permission',          'Static permission catalogue, seeded by migration'),
  ('billing','plan',                 'Public product catalogue'),
  ('platform','admin_user',          'Platform staff; unreachable by the tenant app role (grants revoked)'),
  ('app','rls_exempt_table',         'Meta-table describing this policy'),
  ('app','processed_message',        'Infrastructure idempotency ledger, keyed by message id');

--------------------------------------------------------------------------------
-- Apply RLS to every table that has a tenant_id column.
--
-- Policy shape:
--   USING       — controls which rows are visible to SELECT/UPDATE/DELETE
--   WITH CHECK  — controls which rows may be written
-- Both compare to app.current_tenant_id(), which returns NULL when unset,
-- making the predicate NULL and therefore denying. Fail-closed.
--------------------------------------------------------------------------------
do $$
declare
  r record;
  v_nullable boolean;
  v_using text;
begin
  for r in
    select c.table_schema, c.table_name, c.is_nullable
      from information_schema.columns c
      join information_schema.tables t
        on t.table_schema = c.table_schema and t.table_name = c.table_name
     where c.column_name = 'tenant_id'
       and t.table_type = 'BASE TABLE'
       and c.table_schema in ('app','platform','identity','inventory','crm','sales',
                              'documents','signatures','messaging','publishing',
                              'billing','audit')
       and not exists (
             select 1 from app.rls_exempt_table x
              where x.schema_name = c.table_schema and x.table_name = c.table_name)
     order by c.table_schema, c.table_name
  loop
    v_nullable := (r.is_nullable = 'YES');

    -- A nullable tenant_id means the table also holds platform-scope rows
    -- (audit events, outbox messages, system role templates). Those rows are
    -- deliberately NOT visible to a tenant session: the predicate requires an
    -- exact match, so NULL tenant_id rows are invisible to tenants and are
    -- read only by the platform role.
    v_using := format('tenant_id = app.current_tenant_id()');

    execute format('alter table %I.%I enable row level security', r.table_schema, r.table_name);
    execute format('alter table %I.%I force  row level security', r.table_schema, r.table_name);

    execute format(
      'drop policy if exists %I on %I.%I',
      r.table_name || '_tenant_isolation', r.table_schema, r.table_name);

    execute format(
      'create policy %I on %I.%I for all to mautodesk_app using (%s) with check (%s)',
      r.table_name || '_tenant_isolation', r.table_schema, r.table_name, v_using, v_using);
  end loop;
end
$$;

--------------------------------------------------------------------------------
-- Tables whose tenant key is not literally named tenant_id, handled explicitly.
--------------------------------------------------------------------------------

-- platform.tenant: a tenant may read and update only its own row.
alter table platform.tenant enable row level security;
alter table platform.tenant force  row level security;
create policy tenant_self_isolation on platform.tenant
  for all to mautodesk_app
  using (id = app.current_tenant_id())
  with check (id = app.current_tenant_id());

-- identity.role: a tenant sees its own roles plus the system role templates
-- (tenant_id is null), but may only write its own.
drop policy if exists role_tenant_isolation on identity.role;
create policy role_read on identity.role
  for select to mautodesk_app
  using (tenant_id = app.current_tenant_id() or tenant_id is null);
create policy role_write on identity.role
  for insert to mautodesk_app
  with check (tenant_id = app.current_tenant_id());
create policy role_modify on identity.role
  for update to mautodesk_app
  using (tenant_id = app.current_tenant_id())
  with check (tenant_id = app.current_tenant_id());
create policy role_delete on identity.role
  for delete to mautodesk_app
  using (tenant_id = app.current_tenant_id() and not is_system);

-- documents.template: same pattern — system templates are readable, not writable.
drop policy if exists template_tenant_isolation on documents.template;
create policy template_read on documents.template
  for select to mautodesk_app
  using (tenant_id = app.current_tenant_id() or tenant_id is null);
create policy template_write on documents.template
  for insert to mautodesk_app
  with check (tenant_id = app.current_tenant_id());
create policy template_modify on documents.template
  for update to mautodesk_app
  using (tenant_id = app.current_tenant_id())
  with check (tenant_id = app.current_tenant_id());

-- identity.role_permission has no tenant_id; it inherits its parent's tenancy.
alter table identity.role_permission enable row level security;
alter table identity.role_permission force  row level security;
create policy role_permission_isolation on identity.role_permission
  for all to mautodesk_app
  using (exists (select 1 from identity.role r
                  where r.id = role_id
                    and (r.tenant_id = app.current_tenant_id() or r.tenant_id is null)))
  with check (exists (select 1 from identity.role r
                       where r.id = role_id and r.tenant_id = app.current_tenant_id()));

-- Envelope children carry tenant_id, so they were handled by the loop above.
-- signatures.envelope_document and signer both do; verified by the gap report.

-- platform.impersonation_session: a tenant may READ its own rows — we commit to
-- showing dealers when we accessed their data — but may never write them.
alter table platform.impersonation_session enable row level security;
alter table platform.impersonation_session force  row level security;
create policy impersonation_tenant_read on platform.impersonation_session
  for select to mautodesk_app
  using (tenant_id = app.current_tenant_id());

--------------------------------------------------------------------------------
-- Grants
--------------------------------------------------------------------------------

-- Reference data: read-only.
grant select on
  inventory.vin_decode_cache,
  sales.tax_jurisdiction,
  sales.postal_jurisdiction,
  sales.rule_set,
  identity.permission,
  billing.plan,
  app.rls_exempt_table
  to mautodesk_app;

-- The VIN cache is written by the decode job through a dedicated grant rather
-- than by ordinary request handling.
grant insert, update on inventory.vin_decode_cache to mautodesk_app;

-- Tenant data: full DML, constrained by RLS.
do $$
declare r record;
begin
  for r in
    select table_schema, table_name
      from information_schema.tables
     where table_type = 'BASE TABLE'
       and table_schema in ('app','platform','identity','inventory','crm','sales',
                            'documents','signatures','messaging','publishing','billing')
       and not (table_schema = 'platform' and table_name = 'admin_user')
  loop
    execute format('grant select, insert, update, delete on %I.%I to mautodesk_app',
                   r.table_schema, r.table_name);
  end loop;
end
$$;

-- Append-only tables: revoke what the blanket grant just handed out. The
-- triggers already block mutation, but a grant that says otherwise is a
-- misleading signal in an access review.
revoke update, delete on audit.event                from mautodesk_app;
revoke update, delete on documents.document_version from mautodesk_app;
revoke update, delete on sales.deal_calculation     from mautodesk_app;
revoke update, delete on signatures.audit_entry     from mautodesk_app;
revoke insert, update, delete on platform.impersonation_session from mautodesk_app;
revoke all on platform.admin_user from mautodesk_app;

grant usage, select on all sequences in schema
  app, platform, identity, inventory, crm, sales, documents, signatures,
  messaging, publishing, billing, audit
  to mautodesk_app;

--------------------------------------------------------------------------------
-- Verification
--
-- app.rls_coverage_gaps() returns every table that should be protected and is
-- not. MautoDesk.SecurityTests asserts this returns zero rows. A developer who
-- adds a tenant table without RLS gets a red build, not a production incident.
--------------------------------------------------------------------------------
create or replace function app.rls_coverage_gaps()
returns table (schema_name text, table_name text, problem text)
language sql stable as $$
  with candidates as (
    select t.table_schema::text as s, t.table_name::text as n
      from information_schema.tables t
     where t.table_type = 'BASE TABLE'
       and t.table_schema in ('app','platform','identity','inventory','crm','sales',
                              'documents','signatures','messaging','publishing',
                              'billing','audit')
       and not exists (select 1 from app.rls_exempt_table x
                        where x.schema_name = t.table_schema and x.table_name = t.table_name)
  )
  select c.s, c.n, 'RLS not enabled'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where not pc.relrowsecurity
  union all
  select c.s, c.n, 'RLS not FORCED (table owner would bypass it)'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where pc.relrowsecurity and not pc.relforcerowsecurity
  union all
  select c.s, c.n, 'RLS enabled but no policy defined'
    from candidates c
    join pg_class pc on pc.relname = c.n
    join pg_namespace pn on pn.oid = pc.relnamespace and pn.nspname = c.s
   where pc.relrowsecurity
     and not exists (select 1 from pg_policy p where p.polrelid = pc.oid)
  union all
  select c.s, c.n, 'no tenant_id column and not declared exempt'
    from candidates c
   where not exists (
     select 1 from information_schema.columns col
      where col.table_schema = c.s and col.table_name = c.n and col.column_name = 'tenant_id')
     and not (c.s = 'platform' and c.n in ('tenant','tenant_setting','retention_policy',
                                           'impersonation_session'))
     and not (c.s = 'identity' and c.n = 'role_permission');
$$;

comment on function app.rls_coverage_gaps() is
  'Returns zero rows in a healthy schema. MautoDesk.SecurityTests fails the '
  'build on any row. Do not "fix" a gap by adding the table to '
  'app.rls_exempt_table without a reviewed justification.';

-- Verifies the audit hash chain for a tenant. Run on a schedule; alert on any
-- returned row. A break means history was altered outside the append path.
create or replace function audit.verify_chain(p_tenant_id uuid)
returns table (broken_at_id bigint, occurred_at timestamptz)
language sql stable as $$
  select e.id, e.occurred_at
    from audit.event e
    left join lateral (
      select p.hash from audit.event p
       where p.tenant_id is not distinct from e.tenant_id and p.id < e.id
       order by p.id desc limit 1
    ) prev on true
   where e.tenant_id is not distinct from p_tenant_id
     and e.prev_hash is distinct from prev.hash;
$$;
