# Oklahoma tax research — the field matrix

**Status: two statutes archived, no number in any rule set, the engine still prices nothing.**

`sales.rule_set` for Oklahoma has been a skeleton since `V0007`: `taxBasis: 'UNVERIFIED'`, null
rates, no fees, and the citation `PLACEHOLDER — must cite the governing statute or DMV publication
before approval`. The deal engine loads only approved rows, so it refuses every jurisdiction. That is
correct behaviour, and it will stay that way until each field below has a primary source and a named
reviewer.

This document is the research plan: every field the engine needs, what would answer it, and what is
actually known so far. It exists because "do the tax research" is not a task anyone can pick up,
whereas "find the source for `tradeInCreditPolicy`" is.

---

## 1. Why there is no draft rule set yet

Every readily available account of Oklahoma vehicle tax is a **secondary source**: tax calculators,
legal-publisher reproductions of the statutes, dealer guides, state-senate briefs from the 1990s.
This project does not let a secondary source back a production rule, for the same reason it does not
let one back a compliance rule — a wrong rate here is a wrong figure on a signed retail contract, and
"the calculator said so" is not a defence.

So the first move was the primary text, and the second is knowing precisely which fields it answers.
Writing a half-filled rule set from the two archived sections would have put plausible numbers next to
`UNVERIFIED` ones in the same row, which is exactly how a placeholder becomes a fact.

## 2. What is archived

Both from the Oklahoma State Courts Network, which publishes the statutes as currently in force, and
both carrying 2025 amendments — which is the argument for archiving rather than remembering.

| File | Section | Last amended |
| --- | --- | --- |
| `db/sources/us-ok/ok-68-os-2103-excise-tax.txt` | 68 O.S. § 2103, *Tax for Transfer of Vehicle Ownership* | Laws 2025, HB 1751, c. 330, § 23, emerg. eff. 1 July 2025 |
| `db/sources/us-ok/ok-68-os-2104-vehicle-value.txt` | 68 O.S. § 2104, *Value of Vehicles* | Laws 2025, HB 1183, c. 252, § 1, eff. 1 July 2026 |

Cited with hashes by `V0015`, and re-hashed by CI on every build.

### What they establish

- **New vehicles:** *"The excise tax for new vehicles shall be levied at three and one-fourth percent
  (3 1/4%) of the value of each new vehicle."*
- **Used vehicles:** *"for the year beginning July 1, 2002, and all subsequent years, Twenty Dollars
  ($20.00) on the first One Thousand Five Hundred Dollars ($1,500.00) or less of value of such
  vehicle, and three and one-fourth percent (3 1/4%) of the remaining value of such vehicle."*
- **Off-road ATVs, UTVs and motorcycles:** 4½% of actual sales price *"before any discounts or credits
  are given for a trade-in"*, minimum $5.
- **Certain heavy trucks, trailers and frac tanks:** a flat $10, with pickups, vans and SUVs excepted.
- **The basis:** *"The value of any vehicle… shall be the actual sales price of such a vehicle."*
- **Delinquency:** *"delinquent from and after the thirtieth day after the legal ownership or
  possession of any vehicle is obtained"*, with *"a penalty of One Dollar ($1.00) per day for each
  day of delinquency, but such penalty shall in no event exceed the amount of the tax."*
- **Rounding:** *"In computing the motor vehicle excise tax, the amount collected shall be rounded
  to the nearest dollar."*
- **Excluded transfers without consideration:** between husband and wife, parent and child, and
  between an individual and a revocable express trust.

### The trap in those two sections

**The used-vehicle rate is not a flat percentage.** It is $20 on the first $1,500 plus 3.25% of the
remainder — a two-band computation. The engine's `TaxRuleSet` currently models `StateRate` as a single
`Percentage`, with no concept of a fixed amount on a first band. **Oklahoma's used-vehicle excise tax
cannot be expressed in the engine's present shape at all.** That is an engine change, not a data
question, and it is the single most important finding in this document: discovering it while filling
in a rule set would have been discovering it late.

**Trade-in treatment is not stated for ordinary vehicles.** The off-road rate expressly excludes
trade-in credits; an exclusion written into one rate is not a rule about the others. Do not infer
either way.

## 3. The matrix

Every field `sales.rule_set` and `TaxRuleSet` need. "Answered" means answered *by archived primary
text*, not by a guide.

| Field | Status | What would answer it |
| --- | --- | --- |
| `taxBasis` | **Answered** — "the actual sales price" | 68 O.S. § 2104(B) ✅ archived |
| `stateRate` (new vehicles) | **Answered** — 3.25% of value | 68 O.S. § 2103(A)(1) ✅ archived |
| used-vehicle banding ($20 + 3.25%) | **Answered, unmodellable** | § 2103(A)(1)(c) ✅ archived — needs an engine change, see §4 |
| off-road rate (4.5%, $5 minimum) | **Answered** | § 2103(A)(1) ✅ archived |
| heavy-vehicle flat $10 | **Answered** | § 2103 ✅ archived |
| excise delinquency and penalty | **Answered** | § 2103 ✅ archived — belongs in a compliance rule, not the tax set |
| exempt transfers | **Answered** | § 2103 ✅ archived |
| `tradeInCreditPolicy` + `tradeInCreditCap` | **Open** | Title 68 provisions on taxable consideration, Tax Commission rules, or an OTC publication. Not § 2103/§ 2104 |
| the vehicle **sales tax** alongside the excise tax | **Open** | Title 68 Article 13 sales-tax provisions and their vehicle-specific treatment. Not yet retrieved |
| `localRateSource` (none / destination / origin) | **Open** | Whether municipal and county sales tax reaches a vehicle sale at all, and if so sourced where |
| `rebateTaxable` | **Open** | Whether a manufacturer rebate reduces "actual sales price" |
| `docFeeCap` + `docFeeTaxable` | **Open** | Any statutory or commission limit on a dealer documentation fee, and whether it is taxable. Likely Title 47 or commission rules |
| title fee | **Open** | Title 47 registration provisions |
| registration / tag fees | **Open** | Title 47 — fee schedules vary by vehicle age and type |
| VIN inspection fee ($4) | **Answered** | HB 3148 § L ✅ archived in `V0013` |
| `MaximumTax` | **Open** | Whether Oklahoma caps vehicle tax at all; likely not, but "likely not" is not a citation |
| `AccessoriesTaxable` | **Open** | Whether dealer-added accessories form part of actual sales price |
| `rounding` / `roundingScale` | **Answered** — the excise tax is *"rounded to the nearest dollar"* | ✅ archived. Conflicts with the engine's 2-decimal default, see §4 |

Seven answered, nine open, two of the answered ones unmodellable as the engine stands.

## 4. Engine changes this research has already surfaced

Both are code, not data, and both would otherwise have been found by a CPA wondering why the number
was wrong:

1. **A banded rate.** `TaxRuleSet.StateRate` is one `Percentage`. Oklahoma's used-vehicle excise tax
   is a fixed $20 on a first band plus a percentage on the remainder. The rule-set schema and the
   engine both need a band concept, with a schema version bump — `sales.rule_set.schema_version`
   exists for exactly this.
2. **Rounding to the dollar.** § 2103 says the excise tax is rounded to the nearest dollar, while the
   seeded `roundingScale` is 2. Money is `numeric(14,2)` throughout and that does not change, but the
   *tax* line rounds differently from the other lines, and the engine rounds once at a boundary. Which
   boundary is now a question with a statutory answer.

Neither is urgent — nothing can be priced until a reviewer approves a rule set — but both should be
built before the data arrives, so the first approved Oklahoma rule set is not held up waiting for an
engine that can express it.

## 5. What to do next, in order

1. **Retrieve the remaining primary sources** for the nine open fields. Title 68's sales-tax article
   and Title 47's fee schedules are both on OSCN; dealer documentation fees may be commission rules
   rather than statute. Archive each one the way `V0015` does: hashed, cited, with its amendment
   history.
2. **Make the engine able to express a banded rate and dollar rounding** (§4).
3. **Draft the rule set**, every field citing its archived source, `approved_at` null.
4. **Have a CPA or Oklahoma tax counsel review and approve it.** A different reviewer from the
   compliance pack, against different sources — see
   [`docs/15-counsel-engagement-brief.md`](15-counsel-engagement-brief.md) §3, which puts tax
   expressly out of scope for the compliance engagement.

Until step 4, the engine refuses to price a deal, and
`The_seeded_oklahoma_rule_set_refuses_to_price_a_deal` going red is the system working.
