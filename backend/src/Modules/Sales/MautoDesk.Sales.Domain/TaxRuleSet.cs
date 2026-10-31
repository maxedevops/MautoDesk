using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>How a jurisdiction credits a trade-in against the taxable amount.</summary>
/// <remarks>
/// This single field is worth thousands of dollars on a deal and differs across
/// the launch states, which is exactly why it is data rather than a branch in the
/// engine. The engine implements all three; which one applies is a fact about a
/// jurisdiction on a date, read from an approved, source-cited rule set.
/// </remarks>
public enum TradeInCreditPolicy
{
    /// <summary>No credit. Tax is assessed on the full selling price.</summary>
    None,

    /// <summary>The full trade allowance reduces the taxable amount.</summary>
    Full,

    /// <summary>The allowance reduces the taxable amount up to a stated cap.</summary>
    Capped,
}

/// <summary>Whether a rebate reduces the taxable amount.</summary>
public enum RebateTaxTreatment
{
    /// <summary>The rebate is taxed — tax is computed before it is applied.</summary>
    Taxable,

    /// <summary>The rebate reduces the taxable amount.</summary>
    NotTaxable,
}

/// <summary>A fee the jurisdiction recognises, with whatever limits apply to it.</summary>
/// <param name="Code">The fee code, matching <c>sales.fee_definition.code</c>.</param>
/// <param name="IsTaxable">Whether the fee enters the taxable base.</param>
/// <param name="IsStateFee">A pass-through to the state rather than dealer revenue.</param>
/// <param name="StatutoryCap">The most that may be charged, where a cap exists.</param>
public sealed record FeeRule(
    string Code,
    bool IsTaxable,
    bool IsStateFee,
    Money? StatutoryCap = null);

/// <summary>
/// The tax and fee rules for one jurisdiction over one span of dates.
/// </summary>
/// <remarks>
/// <para>
/// The strongly-typed form of a <c>sales.rule_set</c> row. The database stores
/// <c>rules</c> as jsonb because the shape varies by jurisdiction and modelling
/// every variation relationally produces a schema nobody can read; the engine
/// deserializes it into this record, so it is flexible in storage and typed where
/// it matters — inside the calculation (ADR-0008).
/// </para>
/// <para>
/// <b><see cref="IsApproved"/> is not advisory.</b> The engine refuses to price a
/// deal against an unapproved rule set and returns an error saying so. Every rule
/// set seeded today is unapproved and carries <c>'UNVERIFIED'</c> placeholders,
/// which means the engine currently refuses every jurisdiction — deliberately.
/// Shipping plausible-looking tax numbers sourced from nothing is how a DMS puts
/// a wrong figure on a signed retail contract.
/// </para>
/// </remarks>
public sealed record TaxRuleSet
{
    /// <summary>The <c>sales.rule_set</c> row this came from.</summary>
    public required Guid RuleSetId { get; init; }

    /// <summary>The row's version number, recorded on every snapshot it produces.</summary>
    public required int Version { get; init; }

    /// <summary>The taxing jurisdiction these rules belong to.</summary>
    public required Guid JurisdictionId { get; init; }

    /// <summary>First date these rules govern.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Last date these rules govern, exclusive. Null means open-ended.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>
    /// Whether a named human approved this rule set.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>approved_at is not null</c>. False makes the engine refuse.
    /// </remarks>
    public required bool IsApproved { get; init; }

    /// <summary>The statute, publication or bulletin these rules were read from.</summary>
    public string SourceCitation { get; init; } = string.Empty;

    /// <summary>The state's portion of the sales tax rate.</summary>
    public Percentage StateRate { get; init; } = Percentage.Zero;

    /// <summary>The local portion, which in several states varies by destination address.</summary>
    public Percentage LocalRate { get; init; } = Percentage.Zero;

    /// <summary>How a trade-in is credited against the taxable amount.</summary>
    public TradeInCreditPolicy TradeInCredit { get; init; } = TradeInCreditPolicy.None;

    /// <summary>The cap, where <see cref="TradeInCredit"/> is <see cref="TradeInCreditPolicy.Capped"/>.</summary>
    public Money? TradeInCreditCap { get; init; }

    /// <summary>Whether a rebate reduces the taxable amount.</summary>
    public RebateTaxTreatment RebateTreatment { get; init; } = RebateTaxTreatment.Taxable;

    /// <summary>
    /// A ceiling on the tax itself, where the jurisdiction caps it.
    /// </summary>
    /// <remarks>
    /// Some states assess a flat or capped excise rather than an uncapped
    /// percentage. Modelled here so that adding such a state is a data change.
    /// </remarks>
    public Money? MaximumTax { get; init; }

    /// <summary>Whether dealer-installed accessories are taxed with the vehicle.</summary>
    public bool AccessoriesTaxable { get; init; } = true;

    /// <summary>Fee rules, keyed by code.</summary>
    public IReadOnlyList<FeeRule> Fees { get; init; } = [];

    /// <summary>The combined rate applied to the taxable amount.</summary>
    public Percentage CombinedRate => StateRate + LocalRate;

    /// <summary>True when <paramref name="date"/> falls inside this rule set's window.</summary>
    public bool CoversDate(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date < EffectiveTo.Value);

    /// <summary>Finds the rule for a fee code, if the jurisdiction names one.</summary>
    public FeeRule? FeeFor(string? code) => code is null
        ? null
        : Fees.FirstOrDefault(fee => string.Equals(fee.Code, code, StringComparison.OrdinalIgnoreCase));
}
