using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>
/// One labelled step of the calculation.
/// </summary>
/// <remarks>
/// The pipeline emits one of these per step so the desking screen can show a
/// manager <em>why</em> a number is what it is, and so a dispute can be walked
/// line by line rather than re-derived. The steps are the audit trail of the
/// arithmetic; they are serialized into the snapshot alongside the totals.
/// </remarks>
/// <param name="Order">Position in the pipeline, 1-based.</param>
/// <param name="Code">A stable machine name, e.g. <c>trade.equity</c>.</param>
/// <param name="Label">What a human reads on the screen.</param>
/// <param name="Amount">The step's own amount.</param>
/// <param name="RunningTotal">The subtotal after this step, where one is meaningful.</param>
/// <param name="Note">Why the number is what it is, when that is not obvious.</param>
public sealed record CalculationStep(
    int Order,
    string Code,
    string Label,
    Money Amount,
    Money? RunningTotal = null,
    string? Note = null);

/// <summary>
/// What the engine computed, and how.
/// </summary>
/// <remarks>
/// Serialized verbatim into <c>sales.deal_calculation.output</c>. The columns on
/// that table are a projection of these fields for indexing and reporting — never
/// an independent source of truth, which is why nothing recomputes them.
/// </remarks>
public sealed record DealCalculation
{
    /// <summary>The engine that produced this, so a replay can pin the same one.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>The rule set used, recorded even though the caller supplied it.</summary>
    public required Guid RuleSetId { get; init; }

    /// <summary>The rule set's version.</summary>
    public required int RuleSetVersion { get; init; }

    /// <summary>The selling price after accessories and discounts.</summary>
    public required Money SellingPrice { get; init; }

    /// <summary>Dealer-installed accessories added to the price.</summary>
    public Money Accessories { get; init; } = Money.Zero;

    /// <summary>Dealer discounts taken off the price.</summary>
    public Money Discounts { get; init; } = Money.Zero;

    /// <summary>Rebates and incentives applied.</summary>
    public Money Rebates { get; init; } = Money.Zero;

    /// <summary>The portion of <see cref="Rebates"/> the customer may not qualify for.</summary>
    public Money ConditionalRebates { get; init; } = Money.Zero;

    /// <summary>What the customer is credited for the trade.</summary>
    public Money TradeAllowance { get; init; } = Money.Zero;

    /// <summary>What is owed on the trade.</summary>
    public Money TradePayoff { get; init; } = Money.Zero;

    /// <summary>Allowance less payoff. Negative is negative equity, rolled into the amount financed.</summary>
    public Money TradeEquity { get; init; } = Money.Zero;

    /// <summary>The base the tax rate was applied to.</summary>
    public Money TaxableAmount { get; init; } = Money.Zero;

    /// <summary>The rate applied, recorded so the snapshot does not depend on re-reading the rule set.</summary>
    public Percentage TaxRate { get; init; } = Percentage.Zero;

    /// <summary>The sales tax.</summary>
    public Money SalesTax { get; init; } = Money.Zero;

    /// <summary>All fees, dealer and state.</summary>
    public Money TotalFees { get; init; } = Money.Zero;

    /// <summary>The portion of <see cref="TotalFees"/> passed through to the state.</summary>
    public Money TotalStateFees { get; init; } = Money.Zero;

    /// <summary>Aftermarket products: service contracts, GAP, warranties.</summary>
    public Money TotalProducts { get; init; } = Money.Zero;

    /// <summary>Cash the customer put down.</summary>
    public Money CashDown { get; init; } = Money.Zero;

    /// <summary>Price plus tax, fees and products — before any credits.</summary>
    public required Money TotalSalePrice { get; init; }

    /// <summary>What is left after cash down, rebates and trade equity.</summary>
    public required Money BalanceDue { get; init; }

    /// <summary>The amount financed, or zero on a cash deal.</summary>
    public Money AmountFinanced { get; init; } = Money.Zero;

    /// <summary>The rate quoted, where the deal is financed.</summary>
    public Percentage? Apr { get; init; }

    /// <summary>Term in months, where the deal is financed.</summary>
    public int? TermMonths { get; init; }

    /// <summary>The scheduled payment.</summary>
    public Money? Payment { get; init; }

    /// <summary>How many payments there are.</summary>
    public int? PaymentCount { get; init; }

    /// <summary>Total interest over the term.</summary>
    public Money? FinanceCharge { get; init; }

    /// <summary>What the customer pays in all.</summary>
    public Money? TotalOfPayments { get; init; }

    /// <summary>Which lender arithmetic produced the payment.</summary>
    public PaymentMethodKind? PaymentMethod { get; init; }

    /// <summary>Vehicle cost at the time of the snapshot, where the caller could see it.</summary>
    public Money? VehicleCost { get; init; }

    /// <summary>Gross on the vehicle itself.</summary>
    public Money? FrontGross { get; init; }

    /// <summary>Gross on products and reserve.</summary>
    public Money? BackGross { get; init; }

    /// <summary>Front plus back.</summary>
    public Money? TotalGross { get; init; }

    /// <summary>Amount financed over vehicle value, where both are known.</summary>
    /// <remarks>
    /// A percentage, so 118.5 means the customer is financing 118.5% of the
    /// vehicle. Lenders cap this, which is why it is computed here and consumed
    /// by the lending module rather than the other way round.
    /// </remarks>
    public Percentage? LoanToValue { get; init; }

    /// <summary>The pipeline, step by step.</summary>
    public IReadOnlyList<CalculationStep> Steps { get; init; } = [];

    /// <summary>Things a human should look at, which are not errors.</summary>
    /// <remarks>
    /// A fee over its statutory cap, negative equity beyond a threshold, a
    /// conditional rebate carrying the deal. The engine computes regardless and
    /// says what it noticed; whether any of them <em>block</em> is the regulatory
    /// engine's decision, not the calculator's.
    /// </remarks>
    public IReadOnlyList<string> Advisories { get; init; } = [];
}
