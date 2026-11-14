using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>What a line on the deal is.</summary>
/// <remarks>
/// These are the values <c>sales.deal_line_item.kind</c> accepts. The check
/// constraint in V0004 is the authority; this enum must not drift from it, and
/// <c>DealLineItemKindTests</c> asserts they agree.
/// </remarks>
public enum LineItemKind
{
    /// <summary>A dealer or state fee — doc fee, title, registration.</summary>
    Fee,

    /// <summary>An aftermarket product: paint protection, wheel and tyre.</summary>
    Product,

    /// <summary>A dealer-installed accessory, added to the vehicle price.</summary>
    Accessory,

    /// <summary>A dealer discount off the asking price.</summary>
    Discount,

    /// <summary>A manufacturer or captive rebate assigned to the dealer.</summary>
    Rebate,

    /// <summary>A vehicle service contract.</summary>
    ServiceContract,

    /// <summary>Guaranteed asset protection.</summary>
    Gap,

    /// <summary>An extended warranty.</summary>
    Warranty,

    /// <summary>A manual override of the computed tax, which must be justified.</summary>
    TaxOverride,
}

/// <summary>One line on the deal.</summary>
/// <remarks>
/// <para>
/// <see cref="Amount"/> is always positive. Whether a line adds to or subtracts
/// from the total is decided by its <see cref="Kind"/>, not by its sign — a
/// negative discount and a positive discount would otherwise mean the same thing
/// to a user and opposite things to the engine.
/// </para>
/// <para>
/// <see cref="IsConditional"/> marks an incentive the customer may not qualify
/// for — military, college graduate, loyalty. It is carried separately because
/// advertising rules in several states treat a conditional incentive differently
/// from an unconditional one, and because a deal quoted on an incentive the
/// customer cannot claim is a deal that unwinds at delivery.
/// </para>
/// </remarks>
/// <param name="Kind">What sort of line this is.</param>
/// <param name="Code">A stable code, matching a fee definition where one applies.</param>
/// <param name="Description">What appears on the contract.</param>
/// <param name="Amount">The amount, always positive.</param>
/// <param name="Cost">Dealer cost, where known, for back-end gross.</param>
/// <param name="IsTaxable">Whether this line enters the taxable base.</param>
/// <param name="IsStateFee">A pass-through to the state rather than dealer revenue.</param>
/// <param name="IsConditional">An incentive the customer may not qualify for.</param>
public sealed record DealLineItem(
    LineItemKind Kind,
    string? Code,
    string Description,
    Money Amount,
    Money? Cost = null,
    bool IsTaxable = false,
    bool IsStateFee = false,
    bool IsConditional = false);

/// <summary>A vehicle being traded in.</summary>
/// <param name="Allowance">What the customer is credited, which drives the trade tax credit.</param>
/// <param name="Payoff">What is owed on it, which is added back to the amount financed.</param>
/// <param name="ActualCashValue">What the dealer thinks it is worth, for gross.</param>
public sealed record TradeIn(Money Allowance, Money Payoff, Money? ActualCashValue = null);

/// <summary>How often a payment is due.</summary>
public enum PaymentFrequency
{
    /// <summary>Twelve payments a year.</summary>
    Monthly,

    /// <summary>Twenty-four payments a year, on fixed days of the month.</summary>
    Semimonthly,

    /// <summary>Twenty-six payments a year.</summary>
    Biweekly,

    /// <summary>Fifty-two payments a year.</summary>
    Weekly,
}

/// <summary>Which arithmetic a lender uses to turn a principal into a payment.</summary>
/// <remarks>
/// Not every lender amortizes the same way, and the difference is not academic:
/// on a 60-month subprime deal, add-on interest and simple interest produce
/// payments tens of dollars apart. The lender <em>program version</em> selects
/// this, so replaying a historical deal recomputes it the way it was originally
/// computed rather than the way today's lender would.
/// </remarks>
public enum PaymentMethodKind
{
    /// <summary>The standard amortization formula. The default for retail instalment contracts.</summary>
    StandardAmortization,

    /// <summary>
    /// Simple interest. Interest accrues on the outstanding balance daily; the
    /// scheduled payment is computed by the same amortization formula.
    /// </summary>
    SimpleInterest,

    /// <summary>
    /// Pre-computed, or add-on, interest. The finance charge is calculated on the
    /// original principal for the full term and added before dividing by the
    /// number of payments — which makes the effective rate higher than the
    /// nominal one.
    /// </summary>
    Precomputed,
}

/// <summary>The financing the deal is being quoted on.</summary>
/// <param name="Apr">The rate presented to the customer.</param>
/// <param name="TermMonths">Term in months. The payment count follows from the frequency.</param>
/// <param name="Frequency">How often a payment falls due.</param>
/// <param name="Method">Which lender arithmetic applies.</param>
/// <param name="BuyRate">The lender's rate, where the dealer is participating.</param>
/// <param name="ReserveAmount">Dealer participation, supplied by the lending module.</param>
public sealed record FinanceRequest(
    Percentage Apr,
    int TermMonths,
    PaymentFrequency Frequency = PaymentFrequency.Monthly,
    PaymentMethodKind Method = PaymentMethodKind.StandardAmortization,
    Percentage? BuyRate = null,
    Money? ReserveAmount = null);

/// <summary>
/// Everything the deal engine needs, and nothing it does not.
/// </summary>
/// <remarks>
/// <para>
/// This record is the engine's entire world. It holds no identifiers it does not
/// use, reads no database, and has no reference to a clock — <see cref="DealDate"/>
/// is supplied because the date a deal was written governs which rule set applies,
/// and a value that important must be an input rather than something read from the
/// machine at an unpredictable moment.
/// </para>
/// <para>
/// It is serialized verbatim into <c>sales.deal_calculation.input</c>, which is
/// what makes a disputed 2026 deal replayable byte-for-byte in 2029 against the
/// pinned engine version.
/// </para>
/// </remarks>
public sealed record DealInput
{
    /// <summary>The deal this calculation belongs to.</summary>
    public required Guid DealId { get; init; }

    /// <summary>
    /// The date that governs rule resolution.
    /// </summary>
    /// <remarks>
    /// The date the deal was <em>written</em>, not today. Recomputing a January
    /// deal in July must still use January's rules; taking the current date here
    /// would silently restate history the first time a rate changed.
    /// </remarks>
    public required DateOnly DealDate { get; init; }

    /// <summary>The negotiated selling price, before accessories or discounts.</summary>
    public required Money VehiclePrice { get; init; }

    /// <summary>Dealer cost, for front-end gross. Absent when the caller lacks the permission to see it.</summary>
    public Money? VehicleCost { get; init; }

    /// <summary>Fees, products, accessories, discounts and rebates.</summary>
    public IReadOnlyList<DealLineItem> LineItems { get; init; } = [];

    /// <summary>The trade, where there is one.</summary>
    public TradeIn? Trade { get; init; }

    /// <summary>Cash the customer is putting down.</summary>
    public Money CashDown { get; init; } = Money.Zero;

    /// <summary>The financing, or null for a cash deal.</summary>
    public FinanceRequest? Finance { get; init; }
}
