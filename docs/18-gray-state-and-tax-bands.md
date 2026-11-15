# The GRAY state, banded tax, and a saved place for a signature

Three changes, one purpose: **make the engine able to hold the truth, and stop it
asserting things it does not know.** None of them approves anything, and none
needs a lawyer to land.

---

## 1. GRAY — "we did not check this" is now a verdict

### What was wrong

Every condition operator except `exists` and `not_exists` returned *false* when a
fact was absent. So a rule over a field nobody captured did not fire, produced no
finding, and the deal came back **GREEN**.

That is the most dangerous defect this system can have. It fails silently, it
fails in the direction of saying yes, and it makes **a half-filled deal
indistinguishable from a lawful one**. It was structural, not a bug in any
particular rule — every rule had it.

The test that pinned it is worth quoting, because it had the right instinct and
the wrong assertion:

> *A fact nobody supplied does not satisfy a comparison. Unknown is not false.
> Treating it as a match is how a rule silently stops firing the day an upstream
> field is renamed.*
>
> `run.Results[0].Status.Should().Be(ComplianceStatus.Pass, "the fact was never supplied");`

The remark was a defect report. Nobody read it as one, because the test was green.

### What it is now

Conditions are **three-valued** — `Yes`, `No`, `Unknown` — composed with Kleene
logic:

| Node | Rule |
| --- | --- |
| `all` | a decided **No** wins over an Unknown |
| `any` | a decided **Yes** wins over an Unknown |
| `not` | Unknown negates to Unknown |

Those two precedences are not symmetry for its own sake; they are what stops GRAY
meaning nothing. Without "a decided No wins", every retail-only rule would go
gray on every wholesale deal, so every deal would be gray.

A rule whose conditions come out Unknown produces `ComplianceStatus.Unknown` and
**names the facts it was missing**, because "this rule could not be evaluated"
sends somebody hunting and "the odometer reading is missing" sends them to a
field. The run collects them all into `MissingFacts`, deduplicated and sorted.

Readiness ranks **Red → Gray → Yellow → Green**. A known blocker outranks an
unknown because it is certain; an unknown outranks a warning because the thing
nobody knows may itself be a blocker.

### What GREEN means now

It used to mean "no rule raised a finding", which an absent fact produced just as
easily as a compliant deal. It now means **"every applicable rule was decided, on
complete facts, and none raised a finding"** — a narrower and far more useful
claim.

`RuleEvaluator.Version` goes **1.0.0 → 2.0.0**. Major, because it adds a status
consumers must handle and changes verdicts in the one direction that matters. Runs
already stored under 1.0.0 keep their recorded status: a run records what the
engine of the day concluded, and rewriting history to today's logic would destroy
the only evidence of what the system actually told somebody.

### A present-but-unparseable fact is also Unknown

`"n/a"` where a figure belongs is a different problem from an absent fact, and
still not something to answer "no" to. Something upstream is broken and a human
needs to see it.

---

## 2. Banded tax — Oklahoma could not be expressed at all

68 O.S. § 2103, archived and hashed by `V0015`:

> "Twenty Dollars ($20.00) on the first One Thousand Five Hundred Dollars
> ($1,500.00) or less of value of such vehicle, and three and one-fourth percent
> (3 1/4%) of the remaining value of such vehicle"

`TaxRuleSet.StateRate` was a single `Percentage`. **Oklahoma's used-vehicle excise
tax was not expressible in the engine's shape**, so even a fully approved rule set
could not have priced an Oklahoma deal. The same section adds:

> "In computing the motor vehicle excise tax, the amount collected shall be
> rounded to the nearest dollar."

while the seeded `roundingScale` is 2.

### The shape

`sales.rule_set.schema_version` 2 adds an ordered `bands` array and
`taxRounding`. A band carries an upper bound, a rate on the portion inside it, and
optionally a flat amount:

```json
"bands": [
  { "upTo": "1500.00", "flat": "20.00", "rate": "0" },
  { "upTo": null,      "flat": null,    "rate": "3.25" }
],
"taxRounding": "dollars"
```

A **flat amount is charged in full the moment the base reaches the band**, because
the statute says "or less": a $900 car owes the whole twenty dollars. The obvious
misreading is to prorate it, which would charge $12 — and a tax that is too low is
as wrong as one too high, because the dealer makes up the difference to the state.

A worked example, asserted through the real engine:

```
$20,000 car:  20.00 + 3.25% × 18,500 = 20.00 + 601.25 = 621.25  →  $621
```

### Four ways a band set can be quietly wrong

`TaxRuleSet.BandError` refuses all of them, and the engine and the approval tool
both consult it so there is one definition and not two:

| Refused | Why it matters |
| --- | --- |
| Bands **and** a flat rate | Two taxes; choosing between them is a guess about money on a contract |
| An unbounded band that is not last | Every band above it is unreachable |
| A **bounded top band** | A $70,000 truck is taxed on $50,000 of its value and nothing says so |
| Bands that do not ascend | The arithmetic is nonsense |

The bounded top band is the quiet one: it produces a confident figure that is
simply too small.

### The snapshot records an effective rate

A banded jurisdiction has no single statutory rate, so `TaxRate` on the snapshot
is tax ÷ base. Recording the first band's rate would print **0%** on a $20,000
Oklahoma deal, because the first band is a flat twenty dollars.

### A bug this surfaced in the serializer

Adding bands made an approved banded rule set **unloadable**: the serializer is
strict on approved rows and still demanded a flat `stateRate`, which
`BandError` simultaneously forbids. Caught by the new tests, not by reasoning.
`stateRate` is now required only when there are no bands — but still *read* when
there are, so a rule set carrying both is surfaced rather than silently
bands-only.

`localRate` is no longer required unconditionally either. Plenty of states levy no
local tax on a vehicle sale, and erroring on its absence made those jurisdictions
unapprovable. It is required when `localRateSource` says local tax applies and no
rate is given — the genuinely dangerous combination, which would undercharge every
deal in the state.

---

## 3. The approval register — a saved place for a signature

Engine work must not wait on procurement. An Oklahoma attorney and a CPA are both
still to be retained, and everything they will sign already exists as drafted,
source-cited rows. What did not exist was a durable record of **what is waiting on
whom** — that lived in prose across three documents, which is to say it lived
nowhere a tool could read.

`compliance.pending_approval` is that record. `V0017` creates it and fills it:

| Items | For | What they are |
| --- | --- | --- |
| 10 | Attorney | The drafted rule versions, each needing approve / approve-with-change / reject |
| 7 | Attorney | The flagged readings a developer must not settle |
| 2 | Attorney | The statutory questions the text does not answer |
| 1 | CPA or tax counsel | The drafted Oklahoma excise tax |
| 9 | CPA or tax counsel | The tax fields no primary source has answered |
| 1 | Attorney | Whether the archived sources are still current |

Read it with:

```bash
RULEKEEPER_CONNECTION='...' dotnet run --project backend/src/Modules/Compliance/MautoDesk.Compliance.RuleKeeper -- --register --for attorney
```

### What it is not

**It is not an approval and it cannot become one.** Neither engine reads it. A row
records that a draft is waiting; only an approved version row makes a draft
usable. It also confers no authority: `reviewer_discipline` says what kind of
professional an item needs, not that anybody has been engaged, and there is
deliberately no column in which somebody could record their own approval of their
own draft.

### Append-only, with exactly one permitted transition

A disposition is the most consequential thing a human writes into this system, so
it is not an editable field. The trigger allows **open → resolved** and nothing
else: a resolved item is frozen, and the question, the drafter's reading and the
discipline cannot be touched even while resolving.

That last part matters. **The question as asked is part of the record of the
answer.** A register whose questions can be edited afterwards records nothing
useful about what a reviewer actually agreed to.

A resolution is also all-or-nothing: a date with no name, or a name with no note,
is refused by a check constraint rather than stored as a half-finished audit
entry.

---

## 4. `taxkeeper` — so the tax side can be approved at all

`V0017` makes `sales.rule_set` append-only, which means an approval must be a new
row — and until now nothing could insert one. Adding the trigger without the tool
would have recreated exactly the defect `docs/17` describes: documentation
prescribing an impossible procedure.

So `taxkeeper` is the sibling of `rulekeeper`, in the **Sales** module:

```bash
RULEKEEPER_CONNECTION='...' dotnet run --project backend/src/Modules/Sales/MautoDesk.Sales.TaxKeeper -- \
  --draft 0d000000-0000-4000-8000-000000000001 \
  --version 3 --reviewed-by "Firstname Lastname CPA" \
  --operator "whoever is running this" \
  --effective-from 2026-11-15 \
  --note-file ./tax-sign-off.txt
```

Separate from `rulekeeper` for a boundary reason, not a privilege one: validating
a tax rule set means deserializing it with the Sales module's serializer and
asking `TaxRuleSet.BandError` whether its bands cohere. A tool in the Compliance
module cannot reference those, and reimplementing them would give the approval
path a second, drifting definition of a valid rule set. **Each tool lives in the
module whose law it approves**, and they share one database identity.

Ten checks run before any write. The one worth having is the last:

> **It prices a real deal.** A rule set can satisfy every structural check and
> still produce $0.00 — bands whose rates are all zero, a flat rate left at its
> default. Nothing downstream questions a confident zero. So the tool runs a
> $20,000 cash deal through the **real `DealEngine`** and refuses a rule set that
> taxes it nothing.

Both tools write nothing without `--commit`, take the credential from
`RULEKEEPER_CONNECTION` rather than an argument, and require the sign-off as a
**file** — an approval is a document, and one line is not a review.

### The Oklahoma draft is still not approvable, correctly

`V0016` drafts the excise tax from the two archived statutes and nothing else, so
`tradeInCreditPolicy` and `localRateSource` are still `UNVERIFIED`. `taxkeeper`
refuses to approve a row whose engine-read fields are unverified, so the drafted
row cannot be signed until those questions are answered. Both are register items.

**One finding for the reviewer, recorded on the register item itself:** the
drafted row covers **used** vehicles. The same section levies a flat 3.25% on new
ones, and one `sales.rule_set` row per jurisdiction-and-date cannot hold both. The
product's market is independent used-car dealers, so used-first is the right first
draft — but how new vehicles are expressed is a design question nobody has
answered.

---

## 5. What this did not change

- **Nothing is approved.** Not one rule, not one tax figure. The engine still
  refuses to price an Oklahoma deal and still reports GREEN-meaning-nothing-is-
  approved for compliance.
- **No tax number was invented.** `V0016` carries only what 68 O.S. §§ 2103–2104
  actually say; the nine unanswered fields stay `UNVERIFIED` rather than guessed.
- **No federal rules exist.** Still none: no TILA/Reg Z, Reg B, FTC Used Car Rule,
  Holder Rule, GLBA, OFAC or Form 8300.
- **The `mautodesk_app` platform-scope audit chain** is still the open decision in
  [`docs/17`](17-approval-path.md) §5.

## 6. Where to pick up

1. **Retain the two reviewers.** `rulekeeper --register` is now the work list.
2. **Answer the nine tax fields** from primary sources; each is a register item
   that can be closed independently rather than holding up the whole rule set.
3. **Decide how new vehicles are expressed** — a second rule set, a condition on a
   band, or a vehicle-type dimension on the rule set.
4. **Surface GRAY in the UI** when there is a UI. The API returns
   `overallStatus: "Gray"` and `missingFacts` per result; a client that treats
   anything but `Green` as fine is wrong, and one that treats `Gray` as a failure
   is also wrong. It is a prompt for data entry.
