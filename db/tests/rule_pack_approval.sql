--------------------------------------------------------------------------------
-- Every approved regulatory rule was approved properly.
--
-- Approval is a human act: a named reviewer, against a retrieved source, after
-- reading it. This asserts the shape of that act on whatever is in the database,
-- which makes it safe to run against a long-lived development database where
-- test fixtures may legitimately have approved a rule of their own.
--
-- The stricter assertion — that a FRESHLY MIGRATED database contains no approved
-- rule at all, because a migration must never approve one — lives in CI, where
-- the database is guaranteed to contain nothing but migrations.
--
-- Run: psql -U postgres -d mautodesk -v ON_ERROR_STOP=1 -f rule_pack_approval.sql
--------------------------------------------------------------------------------

\set ON_ERROR_STOP on

do $$
declare
  offenders text;
  approved  int;
  drafts    int;
begin
  ---------------------------------------------------------------------------
  -- An approval with no approver is not an approval. The check constraint on
  -- the table already ties approved_at and approved_by together; this catches
  -- the version of the mistake it cannot see — a name that is blank, or a
  -- placeholder somebody meant to replace.
  ---------------------------------------------------------------------------
  select string_agg(format('%s %s (approved_by=%L)', r.rule_code, v.version, v.approved_by),
                    ', ' order by r.rule_code)
    into offenders
    from compliance.regulatory_rule_version v
    join compliance.regulatory_rule r on r.id = v.rule_id
   where v.approved_at is not null
     and (
       coalesce(btrim(v.approved_by), '') = ''
       or upper(v.approved_by) like '%UNVERIFIED%'
       or upper(v.approved_by) like '%TODO%'
       or upper(v.approved_by) like '%PLACEHOLDER%'
     );

  if offenders is not null then
    raise exception
      'These rule versions are approved but do not name a real reviewer: %', offenders
      using errcode = 'check_violation';
  end if;

  ---------------------------------------------------------------------------
  -- An approved rule must cite an archived source. A rule approved against a
  -- source version whose document was never retrieved was approved against a
  -- URL, which is not the same as reading the law.
  ---------------------------------------------------------------------------
  select string_agg(format('%s %s', r.rule_code, v.version), ', ' order by r.rule_code)
    into offenders
    from compliance.regulatory_rule_version v
    join compliance.regulatory_rule r on r.id = v.rule_id
    join compliance.regulatory_source_version sv on sv.id = v.source_version_id
   where v.approved_at is not null
     and sv.content_location is null;

  if offenders is not null then
    raise exception
      'These rule versions are approved but cite a source whose document was '
      'never archived, so "written against this text" cannot be verified: %', offenders
      using errcode = 'check_violation';
  end if;

  select count(*) into approved
    from compliance.regulatory_rule_version where approved_at is not null;
  select count(*) into drafts
    from compliance.regulatory_rule_version where approved_at is null;

  raise notice
    'Rule pack: % approved, % unapproved. A jurisdiction whose rules are all '
    'drafts reports GREEN because nothing is approved — not because its deals '
    'are compliant.', approved, drafts;
end
$$;
