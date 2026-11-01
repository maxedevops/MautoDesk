using FluentAssertions;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The deal engine, against hand-computed figures.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rates here are fictional.</b> A round 5% on a fixture jurisdiction
/// called <c>ZZ-TEST</c>, chosen because it makes the arithmetic checkable by
/// eye. No real jurisdiction's rate, cap or trade-in treatment appears anywhere
/// in this file — those are regulatory content, and they arrive from a cited
/// primary source with a named approver, not from a test fixture somebody later
/// mistakes for research.
/// </para>
/// <para>
/// Every expected value below is worked in the comment above it. An expectation
/// copied from a previous run of the code under test proves only that the code
/// has not changed.
/// </para>
/// </remarks>
public sealed class DealEngineTests
{
    private static readonly Guid RuleSetId = Guid.Parse("11111111-1111-4111-8111-000000000001");
    private static readonly Guid JurisdictionId = Guid.Parse("22222222-2222-4222-8222-000000000002");
    private static readonly DateOnly DealDate = new(2026, 6, 15);

    // ---------------------------------------------------------------------
    // Refusals. The engine says no more often than it says a number, and each
    // refusal is preferable to a plausible wrong figure.
    // ---------------------------------------------------------------------

    /// <summary>
    /// An unapproved rule set produces no price at all.
    /// </summary>
    /// <remarks>
    /// This is the single most important test in the file. Every rule set seeded
    /// today is unapproved with <c>'UNVERIFIED'</c> placeholders, so this is the
    /// behaviour a developer meets first — and the temptation to relax it to make
    /// a demo work is exactly what it exists to resist.
    /// </remarks>
    [Fact]
    public void An_unapproved_rule_set_refuses_to_price_the_deal()
    {
        var result = DealEngine.Calculate(CashDeal(), Rules(approved: false));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("deal.rule_set_unapproved");
        result.Error.Message.Should().Contain("named approver");
    }

    /// <summary>A rule set that does not cover the deal date is not used anyway.</summary>
    /// <remarks>
    /// A deal is priced with the rules in force on the day it was written. Falling
    /// back to the nearest rule set would silently restate history the first time a
    /// rate changed.
    /// </remarks>
    [Fact]
    public void A_rule_set_outside_its_window_is_refused()
    {
        var rules = Rules() with
        {
            EffectiveFrom = new DateOnly(2027, 1, 1),
        };

        var result = DealEngine.Calculate(CashDeal(), rules);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("deal.rule_set_not_effective");
    }

    /// <summary>A negative line amount is rejected rather than quietly flipped.</summary>
    [Fact]
    public void Line_items_carry_no_sign()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.Discount, "DISC", "Discount", -500m)],
        };

        var result = DealEngine.Calculate(input, Rules());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("deal.negative_line_item");
    }

    /// <summary>Financing a negative balance is a data error, not a $0 payment.</summary>
    /// <remarks>
    /// Cash down and trade equity exceeding the sale price is a real thing to
    /// enter by mistake. Treating it as zero would produce a plausible-looking
    /// contract for a loan that does not exist.
    /// </remarks>
    [Fact]
    public void A_negative_amount_financed_is_refused()
    {
        var input = FinancedDeal() with { CashDown = Money.FromDecimal(50_000m) };

        var result = DealEngine.Calculate(input, Rules());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("deal.negative_amount_financed");
    }

    /// <summary>A cash deal may legitimately end with money owed back to the customer.</summary>
    [Fact]
    public void A_cash_deal_may_end_with_a_refund_due()
    {
        var input = CashDeal() with { CashDown = Money.FromDecimal(25_000m) };

        var calculation = Succeeds(input, Rules());

        // 20,000 + 1,000 tax = 21,000; less 25,000 down = −4,000.
        calculation.BalanceDue.Amount.Should().Be(-4_000m);
    }

    [Fact]
    public void A_financed_deal_needs_a_term()
    {
        var input = FinancedDeal() with
        {
            Finance = new FinanceRequest(Percentage.FromPercent(6m), 0),
        };

        DealEngine.Calculate(input, Rules()).Error!.Code.Should().Be("deal.invalid_term");
    }

    // ---------------------------------------------------------------------
    // The tax base. The most jurisdiction-dependent number in the deal, and the
    // reason the rule set is data rather than a branch in the engine.
    // ---------------------------------------------------------------------

    /// <summary>Cash deal, no trade, no fees: 20,000 at 5% is 1,000 of tax.</summary>
    [Fact]
    public void A_plain_cash_deal_taxes_the_full_price()
    {
        var calculation = Succeeds(CashDeal(), Rules());

        calculation.SellingPrice.Amount.Should().Be(20_000m);
        calculation.TaxableAmount.Amount.Should().Be(20_000m);
        calculation.SalesTax.Amount.Should().Be(1_000m);
        calculation.TotalSalePrice.Amount.Should().Be(21_000m);
        calculation.BalanceDue.Amount.Should().Be(21_000m);
        calculation.AmountFinanced.Should().Be(Money.Zero, "a cash deal finances nothing");
    }

    /// <summary>
    /// A full trade credit taxes price less allowance.
    /// </summary>
    /// <remarks>
    /// 20,000 − 8,000 allowance = 12,000 taxable; at 5% that is 600.
    /// Total sale price 20,600; less 5,000 of equity leaves 15,600 due.
    /// </remarks>
    [Fact]
    public void A_full_trade_credit_reduces_the_taxable_amount()
    {
        var input = CashDeal() with
        {
            Trade = new TradeIn(Money.FromDecimal(8_000m), Money.FromDecimal(3_000m)),
        };

        var calculation = Succeeds(input, Rules(trade: TradeInCreditPolicy.Full));

        calculation.TaxableAmount.Amount.Should().Be(12_000m);
        calculation.SalesTax.Amount.Should().Be(600m);
        calculation.TradeEquity.Amount.Should().Be(5_000m);
        calculation.TotalSalePrice.Amount.Should().Be(20_600m);
        calculation.BalanceDue.Amount.Should().Be(15_600m);
    }

    /// <summary>A capped credit stops at the cap: 8,000 allowance, 5,000 cap.</summary>
    /// <remarks>20,000 − 5,000 = 15,000 taxable; at 5% that is 750.</remarks>
    [Fact]
    public void A_capped_trade_credit_stops_at_the_cap()
    {
        var input = CashDeal() with
        {
            Trade = new TradeIn(Money.FromDecimal(8_000m), Money.Zero),
        };

        var rules = Rules(trade: TradeInCreditPolicy.Capped) with
        {
            TradeInCreditCap = Money.FromDecimal(5_000m),
        };

        var calculation = Succeeds(input, rules);

        calculation.TaxableAmount.Amount.Should().Be(15_000m);
        calculation.SalesTax.Amount.Should().Be(750m);
    }

    /// <summary>Where a jurisdiction gives no credit, the trade does not touch the tax.</summary>
    [Fact]
    public void No_trade_credit_taxes_the_whole_price()
    {
        var input = CashDeal() with
        {
            Trade = new TradeIn(Money.FromDecimal(8_000m), Money.Zero),
        };

        var calculation = Succeeds(input, Rules(trade: TradeInCreditPolicy.None));

        calculation.TaxableAmount.Amount.Should().Be(20_000m);
        calculation.SalesTax.Amount.Should().Be(1_000m);
    }

    /// <summary>
    /// Negative equity is financed on top, and is called out.
    /// </summary>
    /// <remarks>
    /// Allowance 5,000, payoff 9,000 → equity −4,000. Full credit taxes
    /// 20,000 − 5,000 = 15,000 → 750 tax → 20,750 total. Subtracting a negative
    /// equity adds it: 20,750 + 4,000 = 24,750 due.
    /// </remarks>
    [Fact]
    public void Negative_equity_is_added_to_what_is_owed()
    {
        var input = CashDeal() with
        {
            Trade = new TradeIn(Money.FromDecimal(5_000m), Money.FromDecimal(9_000m)),
        };

        var calculation = Succeeds(input, Rules(trade: TradeInCreditPolicy.Full));

        calculation.TradeEquity.Amount.Should().Be(-4_000m);
        calculation.TotalSalePrice.Amount.Should().Be(20_750m);
        calculation.BalanceDue.Amount.Should().Be(24_750m);
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("negative equity"));
    }

    /// <summary>A non-taxable rebate comes off the base; a taxable one does not.</summary>
    /// <remarks>
    /// Not taxable: 20,000 − 2,000 = 18,000 → 900 tax → 20,900 total → 18,900 due.
    /// Taxable: 20,000 → 1,000 tax → 21,000 total → 19,000 due.
    /// </remarks>
    [Fact]
    public void Rebate_tax_treatment_changes_the_base_and_the_total()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.Rebate, "REBATE", "Manufacturer rebate", 2_000m)],
        };

        var untaxed = Succeeds(input, Rules(rebates: RebateTaxTreatment.NotTaxable));
        untaxed.TaxableAmount.Amount.Should().Be(18_000m);
        untaxed.SalesTax.Amount.Should().Be(900m);
        untaxed.BalanceDue.Amount.Should().Be(18_900m);

        var taxed = Succeeds(input, Rules(rebates: RebateTaxTreatment.Taxable));
        taxed.TaxableAmount.Amount.Should().Be(20_000m);
        taxed.SalesTax.Amount.Should().Be(1_000m);
        taxed.BalanceDue.Amount.Should().Be(19_000m);
    }

    /// <summary>
    /// A conditional incentive reduces what is owed but not the tax base.
    /// </summary>
    /// <remarks>
    /// The customer may not qualify. Deducting it from the tax base before they do
    /// understates the tax, and being conservative about a tax base is the correct
    /// direction to err. It still reduces the balance, because that is what the
    /// deal is quoted at — hence the advisory.
    /// </remarks>
    [Fact]
    public void A_conditional_incentive_does_not_reduce_the_tax_base()
    {
        var input = CashDeal() with
        {
            LineItems =
            [
                Line(LineItemKind.Rebate, "MIL", "Military appreciation", 2_000m, conditional: true),
            ],
        };

        var calculation = Succeeds(input, Rules(rebates: RebateTaxTreatment.NotTaxable));

        calculation.TaxableAmount.Amount.Should().Be(20_000m, "eligibility is not established yet");
        calculation.SalesTax.Amount.Should().Be(1_000m);
        calculation.ConditionalRebates.Amount.Should().Be(2_000m);
        calculation.BalanceDue.Amount.Should().Be(19_000m);
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("conditional"));
    }

    /// <summary>A discount reduces the selling price, and therefore the tax.</summary>
    /// <remarks>20,000 − 1,500 = 18,500 → 925 tax → 19,425 total.</remarks>
    [Fact]
    public void A_dealer_discount_reduces_the_price_and_the_tax()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.Discount, "DISC", "Sale event discount", 1_500m)],
        };

        var calculation = Succeeds(input, Rules());

        calculation.Discounts.Amount.Should().Be(1_500m);
        calculation.SellingPrice.Amount.Should().Be(18_500m);
        calculation.SalesTax.Amount.Should().Be(925m);
        calculation.TotalSalePrice.Amount.Should().Be(19_425m);
    }

    /// <summary>Accessories are added to the price, and taxed or not per the rule set.</summary>
    /// <remarks>
    /// Taxable: 21,200 base → 1,060 tax → 22,260 total.
    /// Not taxable: base 20,000 → 1,000 tax → 21,200 + 1,000 = 22,200 total.
    /// </remarks>
    [Fact]
    public void Accessory_taxability_is_a_rule_set_decision()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.Accessory, "TINT", "Window tint", 1_200m)],
        };

        var taxed = Succeeds(input, Rules());
        taxed.SellingPrice.Amount.Should().Be(21_200m);
        taxed.TaxableAmount.Amount.Should().Be(21_200m);
        taxed.SalesTax.Amount.Should().Be(1_060m);
        taxed.TotalSalePrice.Amount.Should().Be(22_260m);

        var untaxed = Succeeds(input, Rules() with { AccessoriesTaxable = false });
        untaxed.TaxableAmount.Amount.Should().Be(20_000m);
        untaxed.SalesTax.Amount.Should().Be(1_000m);
        untaxed.TotalSalePrice.Amount.Should().Be(22_200m);
    }

    /// <summary>
    /// A taxable fee joins the base; a state fee is a pass-through.
    /// </summary>
    /// <remarks>
    /// Doc fee 500 taxable, title fee 11 state and untaxed.
    /// Base 20,000 + 500 = 20,500 → 1,025 tax.
    /// Fees total 511, of which 11 is the state's.
    /// Total 20,000 + 1,025 + 511 = 21,536.
    /// </remarks>
    [Fact]
    public void Fees_are_taxed_and_classified_by_the_rule_set()
    {
        var input = CashDeal() with
        {
            LineItems =
            [
                Line(LineItemKind.Fee, "DOC", "Documentary fee", 500m),
                Line(LineItemKind.Fee, "TITLE", "Title fee", 11m),
            ],
        };

        var rules = Rules() with
        {
            Fees =
            [
                new FeeRule("DOC", IsTaxable: true, IsStateFee: false),
                new FeeRule("TITLE", IsTaxable: false, IsStateFee: true),
            ],
        };

        var calculation = Succeeds(input, rules);

        calculation.TaxableAmount.Amount.Should().Be(20_500m);
        calculation.SalesTax.Amount.Should().Be(1_025m);
        calculation.TotalFees.Amount.Should().Be(511m);
        calculation.TotalStateFees.Amount.Should().Be(11m);
        calculation.TotalSalePrice.Amount.Should().Be(21_536m);
    }

    /// <summary>
    /// A fee over its cap is reported, not blocked.
    /// </summary>
    /// <remarks>
    /// The calculator notices. Whether exceeding a cap <em>prevents</em> the deal
    /// is a regulatory decision, made by the compliance engine against a rule with
    /// a citation — not something the arithmetic gets to decide.
    /// </remarks>
    [Fact]
    public void A_fee_above_its_statutory_cap_is_an_advisory_not_a_refusal()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.Fee, "DOC", "Documentary fee", 500m)],
        };

        var rules = Rules() with
        {
            Fees = [new FeeRule("DOC", IsTaxable: false, IsStateFee: false, Money.FromDecimal(400m))],
        };

        var calculation = Succeeds(input, rules);

        calculation.TotalFees.Amount.Should().Be(500m, "the engine reports what was charged");
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("above the"));
    }

    /// <summary>Where a jurisdiction caps the tax itself, the cap wins.</summary>
    [Fact]
    public void A_maximum_tax_caps_the_computed_amount()
    {
        var rules = Rules() with { MaximumTax = Money.FromDecimal(750m) };

        var calculation = Succeeds(CashDeal(), rules);

        calculation.SalesTax.Amount.Should().Be(750m, "1,000 would have been computed");
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("capped"));
    }

    /// <summary>An override replaces the computed tax and says so.</summary>
    /// <remarks>
    /// An out-of-state or exempt sale is real. Modelled as a line item so the
    /// override appears on the contract and in the audit trail, rather than being
    /// a number somebody typed over the top.
    /// </remarks>
    [Fact]
    public void A_tax_override_replaces_the_computed_figure()
    {
        var input = CashDeal() with
        {
            LineItems = [Line(LineItemKind.TaxOverride, "EXEMPT", "Out-of-state delivery", 0m)],
        };

        var calculation = Succeeds(input, Rules());

        calculation.SalesTax.Should().Be(Money.Zero);
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("overridden"));
    }

    /// <summary>Credits beyond the price floor the tax base at zero, not below it.</summary>
    [Fact]
    public void The_taxable_amount_never_goes_negative()
    {
        var input = CashDeal() with
        {
            Trade = new TradeIn(Money.FromDecimal(30_000m), Money.Zero),
        };

        var calculation = Succeeds(input, Rules(trade: TradeInCreditPolicy.Full));

        calculation.TaxableAmount.Should().Be(Money.Zero);
        calculation.SalesTax.Should().Be(Money.Zero);
    }

    // ---------------------------------------------------------------------
    // Financing and gross.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A financed deal at 0% tax: 25,000 over 60 months at 6% is $483.32.
    /// </summary>
    /// <remarks>
    /// The tax rate is zeroed so the amount financed is exactly the anchor figure
    /// from <c>PaymentMethodTests</c>, which makes this a test of the engine's
    /// plumbing rather than a second, weaker test of the amortization formula.
    /// </remarks>
    [Fact]
    public void A_financed_deal_carries_the_payment_through_to_the_snapshot()
    {
        var input = FinancedDeal() with { VehiclePrice = Money.FromDecimal(25_000m) };
        var rules = Rules() with { StateRate = Percentage.Zero, LocalRate = Percentage.Zero };

        var calculation = Succeeds(input, rules);

        calculation.AmountFinanced.Amount.Should().Be(25_000m);
        calculation.Payment!.Value.Amount.Should().Be(483.32m);
        calculation.PaymentCount.Should().Be(60);
        calculation.FinanceCharge!.Value.Amount.Should().Be(3_999.20m);
        calculation.TotalOfPayments!.Value.Amount.Should().Be(28_999.20m);
        calculation.PaymentMethod.Should().Be(PaymentMethodKind.StandardAmortization);
        calculation.LoanToValue!.Value.Percent.Should().Be(100m);
    }

    /// <summary>Financing more than the car is worth is called out.</summary>
    /// <remarks>
    /// Price 10,000, tax 500 → 10,500; plus 4,000 of negative equity → 14,500
    /// financed against a 10,000 vehicle, which is 145%.
    /// </remarks>
    [Fact]
    public void An_over_advanced_structure_is_flagged()
    {
        var input = FinancedDeal() with
        {
            VehiclePrice = Money.FromDecimal(10_000m),
            Trade = new TradeIn(Money.Zero, Money.FromDecimal(4_000m)),
        };

        var calculation = Succeeds(input, Rules(trade: TradeInCreditPolicy.None));

        calculation.AmountFinanced.Amount.Should().Be(14_500m);
        calculation.LoanToValue!.Value.Percent.Should().Be(145m);
        calculation.Advisories.Should().Contain(advisory => advisory.Contains("Loan to value"));
    }

    /// <summary>
    /// Gross separates dealer revenue from what is passed through to the state.
    /// </summary>
    /// <remarks>
    /// Front: 20,000 selling − 16,000 cost = 4,000.
    /// Back: GAP margin (900 − 300 = 600) + dealer fees (511 total − 11 state = 500)
    /// + reserve 350 = 1,450. Total 5,450.
    /// </remarks>
    [Fact]
    public void Gross_counts_dealer_fees_and_excludes_state_fees()
    {
        var input = CashDeal() with
        {
            VehicleCost = Money.FromDecimal(16_000m),
            LineItems =
            [
                Line(LineItemKind.Fee, "DOC", "Documentary fee", 500m),
                Line(LineItemKind.Fee, "TITLE", "Title fee", 11m),
                Line(LineItemKind.Gap, "GAP", "GAP coverage", 900m, cost: 300m),
            ],
            Finance = new FinanceRequest(
                Percentage.FromPercent(6m), 60, ReserveAmount: Money.FromDecimal(350m)),
        };

        var rules = Rules() with
        {
            Fees =
            [
                new FeeRule("DOC", IsTaxable: false, IsStateFee: false),
                new FeeRule("TITLE", IsTaxable: false, IsStateFee: true),
            ],
        };

        var calculation = Succeeds(input, rules);

        calculation.FrontGross!.Value.Amount.Should().Be(4_000m);
        calculation.BackGross!.Value.Amount.Should().Be(1_450m);
        calculation.TotalGross!.Value.Amount.Should().Be(5_450m);
    }

    /// <summary>Without a cost, gross is absent rather than zero.</summary>
    /// <remarks>
    /// Zero would be a number a manager might act on. Null says "you are not being
    /// shown this", which is the truth when the caller lacks
    /// <c>inventory.cost.read</c>.
    /// </remarks>
    [Fact]
    public void Gross_is_absent_when_the_cost_is_not_supplied()
    {
        var calculation = Succeeds(CashDeal(), Rules());

        calculation.FrontGross.Should().BeNull();
        calculation.TotalGross.Should().BeNull();
    }

    /// <summary>A product with no known cost contributes no margin, rather than all of it.</summary>
    [Fact]
    public void An_unknown_product_cost_does_not_inflate_back_end_gross()
    {
        var input = CashDeal() with
        {
            VehicleCost = Money.FromDecimal(16_000m),
            LineItems = [Line(LineItemKind.ServiceContract, "VSC", "Service contract", 1_800m)],
        };

        var calculation = Succeeds(input, Rules());

        calculation.BackGross!.Value.Should().Be(Money.Zero, "the 1,800 might be entirely cost");
    }

    // ---------------------------------------------------------------------
    // Determinism and the trace.
    // ---------------------------------------------------------------------

    /// <summary>The same inputs always produce the same figures.</summary>
    /// <remarks>
    /// The property the whole snapshot design rests on. If this ever fails, a
    /// replayed deal cannot be trusted to reproduce the contract that was signed.
    /// </remarks>
    [Fact]
    public void The_engine_is_deterministic()
    {
        var input = FinancedDeal();
        var rules = Rules();

        var first = Succeeds(input, rules);
        var second = Succeeds(input, rules);

        second.Should().BeEquivalentTo(first);
    }

    /// <summary>Every step is numbered in order, so the trace reads as a sequence.</summary>
    [Fact]
    public void The_pipeline_emits_an_ordered_trace()
    {
        var calculation = Succeeds(FinancedDeal(), Rules());

        calculation.Steps.Should().NotBeEmpty();
        calculation.Steps.Select(step => step.Order)
            .Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        calculation.Steps.Should().Contain(step => step.Code == "vehicle.price");
        calculation.Steps.Should().Contain(step => step.Code == "tax.basis");
        calculation.Steps.Should().Contain(step => step.Code == "finance.payment");
    }

    /// <summary>The snapshot records which rule set produced it.</summary>
    [Fact]
    public void The_calculation_pins_its_rule_set_and_engine_version()
    {
        var calculation = Succeeds(CashDeal(), Rules());

        calculation.RuleSetId.Should().Be(RuleSetId);
        calculation.RuleSetVersion.Should().Be(3);
        calculation.EngineVersion.Should().Be(DealEngine.Version);
        calculation.TaxRate.Percent.Should().Be(5m, "the rate is recorded, not re-read later");
    }

    // ---------------------------------------------------------------------
    // Fixtures. Fictional throughout.
    // ---------------------------------------------------------------------

    private static DealCalculation Succeeds(DealInput input, TaxRuleSet rules)
    {
        var result = DealEngine.Calculate(input, rules);

        result.IsSuccess.Should().BeTrue(
            "the engine refused: {0}", result.Error?.Message ?? "no error");

        return result.Value!;
    }

    private static DealInput CashDeal() => new()
    {
        DealId = Guid.Parse("33333333-3333-4333-8333-000000000003"),
        DealDate = DealDate,
        VehiclePrice = Money.FromDecimal(20_000m),
    };

    private static DealInput FinancedDeal() => CashDeal() with
    {
        Finance = new FinanceRequest(Percentage.FromPercent(6m), 60),
    };

    /// <summary>A fixture rule set. 5% flat, on a jurisdiction that does not exist.</summary>
    private static TaxRuleSet Rules(
        bool approved = true,
        TradeInCreditPolicy trade = TradeInCreditPolicy.None,
        RebateTaxTreatment rebates = RebateTaxTreatment.Taxable) => new()
        {
            RuleSetId = RuleSetId,
            Version = 3,
            JurisdictionId = JurisdictionId,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            IsApproved = approved,
            SourceCitation = "Fixture only. Not a real jurisdiction, not a real rate.",
            StateRate = Percentage.FromPercent(5m),
            LocalRate = Percentage.Zero,
            TradeInCredit = trade,
            RebateTreatment = rebates,
        };

    private static DealLineItem Line(
        LineItemKind kind,
        string code,
        string description,
        decimal amount,
        decimal? cost = null,
        bool conditional = false) =>
        new(kind, code, description, Money.FromDecimal(amount),
            cost is null ? null : Money.FromDecimal(cost.Value),
            IsConditional: conditional);
}
