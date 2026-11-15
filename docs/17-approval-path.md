# The approval path

How an approved regulatory rule gets into this system, and why it is a command-line tool rather than
an endpoint.

---

## 1. The problem it solves

Until `V0014` there was no way to approve a rule at all. Three documents instructed a reviewer to
"set `status`, `approved_at` and `approved_by`", and that is impossible:

- `compliance.regulatory_rule_version` carries `app.deny_mutation()` on UPDATE and DELETE, so no
  existing row's approval columns can ever be changed — by anyone, superuser included.
- `mautodesk_app` holds `SELECT` and nothing else on the law tables. `V0010` says why: *"a
  compromised application cannot rewrite the law it is evaluated against."*

The design was right; only the procedure was wrong. **An approval is a new version row**, inserted
already carrying `approved_at` and `approved_by`, which the exclusion constraint then holds to
non-overlapping effective dates. The invariant tests have always done it that way. What was missing
was somebody able to perform that insert.

## 2. Why not an endpoint

The review asked for a permissioned endpoint. That would mean the API process holds a credential
that can write the law, and anything that compromises a request handler inherits it. The reviewer's
signature is the most security-sensitive act in this product, and it does not belong on the same
credential that serves HTTP.

So the approval runs the way the outbox dispatcher does: **its own database identity, used by its
own process, reachable from nowhere.**

`mautodesk_rulekeeper` can do exactly two things — insert a `regulatory_rule_version`, and append one
kind of platform-scope audit event. It holds no UPDATE or DELETE anywhere, cannot author a rule, a
source or a test, and cannot read a deal, a vehicle or a customer. `db/tests/rulekeeper_grants.sql`
asserts each of those in CI rather than trusting the grant list to stay true.

An endpoint remains possible later, once there is an admin screen to call it. It would be a
deliberate widening, decided on its own merits, and it is not needed to unblock a first approval.

## 3. The tool

`backend/src/Modules/Compliance/MautoDesk.Compliance.RuleKeeper` builds as `rulekeeper`. It lives
inside the Compliance module so that referencing that module's Domain and Application projects is
intra-module and allowed.

```bash
RULEKEEPER_CONNECTION='Host=...;Username=mautodesk_rulekeeper;Password=...' \
  dotnet run --project backend/src/Modules/Compliance/MautoDesk.Compliance.RuleKeeper -- \
  --draft 0b000000-0000-4000-8000-000000000007 \
  --version 1.0.0 \
  --reviewed-by "Firstname Lastname" \
  --operator "whoever is running this" \
  --effective-from 2026-11-01 \
  --note-file ./sign-off-ok-temp-tag-fleet-001.txt \
  --repo-root .
```

Two defaults are deliberate:

- **It writes nothing without `--commit`.** The default behaviour of a tool that can write the law
  is to tell you what it would do.
- **The connection string comes from the environment, never an argument**, so a credential does not
  land in shell history or a process list.

The sign-off arrives as a **file** rather than a string. An approval is a document: it gets written,
read and kept. Typing it between quotes encourages one line, and one line is not a legal review. The
file's full path is recorded on the audit event, and its text is stored on the approved row above the
draft's own note — so the `INTERPRETATION TO CONFIRM` flags the reviewer was asked to settle stay
visible next to the answer.

## 4. What it checks before it writes

None of these are legal judgements. Every one is mechanical, and each corresponds to a mistake that
is easy to make at a keyboard and expensive to find later. A reviewer's sign-off cannot waive them,
because what they protect is the ability to show afterwards what was approved and against what.

| Check | Refuses when |
| --- | --- |
| The draft is a draft | it is already approved — a sign of a wrong id |
| The reviewer is named | blank, too short, or containing `UNVERIFIED`/`TODO`/`PLACEHOLDER` |
| The approved version is a new version | it reuses the draft's version, or one already on the rule |
| The effective period is a period | `effective_to` is not after `effective_from` |
| The source is archived | the cited source version has no `content_location` |
| The archived source still hashes | with `--repo-root`, the file no longer matches its recorded sha256 |
| The definition parses | the evaluator cannot read the rule, so approving it activates something that never fires |
| The test corpus proves something | no cases, or only firing cases, or only non-firing ones |
| Every stored case agrees | the rule does not do what its own documented cases say |
| Business-day deadlines have a calendar | a year the rule governs has no `jurisdiction_holiday` rows |
| No approved version covers these dates | the period overlaps an existing approved version |

The last five are the ones a hand-written `INSERT` would skip. "Every stored case agrees" runs the
cases through the **real** `RuleEvaluator`, the same engine CI uses, so the tool's verdict and CI's
verdict cannot drift apart.

`ApprovalChecksTests` covers all of it: 21 tests, each one a variation on a single well-formed
approval.

## 5. The audit entry, and a defect it exposed

The approval and its audit event commit in one transaction. An approval nobody can see in the ledger
is the failure this path exists to avoid.

Writing that event surfaced a real trap. `audit.chain_event()` runs BEFORE INSERT **as the calling
role** and reads the previous row to compute `prev_hash`:

```sql
select e.hash into v_prev from audit.event e
 where e.tenant_id is not distinct from new.tenant_id
 order by e.id desc limit 1;
```

`audit.event` is FORCE row-level security. A role that may INSERT but cannot SELECT does not get an
error from that query — **it gets zero rows**, so `prev_hash` is NULL, so the row claims to begin the
chain, and `audit.verify_chain()` then reports the chain broken at every such row. An append-only
ledger whose links are quietly absent is worse than no ledger, because it still looks like evidence.

So `mautodesk_rulekeeper` has a SELECT policy scoped to platform-scope rows (`tenant_id is null`) —
events like this one, which are not any dealership's data — and
`db/tests/rulekeeper_grants.sql` asserts that a second event actually links to the first.

### The same defect applies to `mautodesk_app`, and is not fixed here

`mautodesk_app`'s policy is `tenant_id = app.current_tenant_id()`, which is never true when
`tenant_id` is null. So **any platform-scope audit event the API writes also gets
`prev_hash = NULL`**, and `audit.verify_chain(NULL)` would report that chain broken at every row
after the first.

It is latent rather than live — nothing writes platform-scope events through the API yet — but it is
a defect waiting for the first feature that does. It is not fixed in `V0014` because the fix is a
policy decision, not a migration: `V0006` states that platform rows are *"deliberately NOT visible
to a tenant session"*, and letting `mautodesk_app` read them reverses that on purpose. Someone should
decide it rather than have a migration decide it for them. The options are a narrow SELECT policy for
platform rows, a `SECURITY DEFINER` chain function owned by a role that is exempt from FORCE RLS, or
routing platform-scope events through a dedicated identity the way this one is.

## 6. What it still does not do

- **It cannot author the law.** Only approve a draft that already exists as a reviewed change to
  `db/migrations`. Composing a rule and approving it are different acts by different people, and they
  keep different credentials.
- **It cannot withdraw an approval.** The table is append-only; a correction is a further version
  with its own effective dates. There is deliberately no `--unapprove`.
- **It does not know whether the reading is right.** Nothing in software can. That is the reviewer's
  work, and §4 exists so that work is the only thing left to do.
