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

/// <summary>How the tax line itself is rounded.</summary>
/// <remarks>
/// Separate from <c>roundingScale</c>, which governs the deal's money generally.
/// Oklahoma is the reason this exists: 68 O.S. § 2103 says the excise tax
/// "shall be rounded to the nearest dollar", while every other line on the same
/// contract is to the cent. One jurisdiction rounding one line differently is a
/// fact about that jurisdiction, so it is data.
/// </remarks>
public enum TaxRounding
{
    /// <summary>To the cent, like every other figure on the deal.</summary>
    Cents,

    /// <summary>To the nearest whole dollar, as some excise statutes require.</summary>
    Dollars,
}

/// <summary>
/// One band of a graduated tax.
/// </summary>
/// <param name="UpTo">
/// The upper bound of this band, exclusive of the next one. Null means
/// unbounded, which only the last band may be.
/// </param>
/// <param name="Rate">The rate on the portion of the base falling inside this band.</param>
/// <param name="Flat">
/// A fixed charge for this band, levied in full the moment the base reaches the
/// band at all. Oklahoma's "$20.00 on the first $1,500.00 <b>or less</b>" is
/// exactly this: a nine-hundred-dollar car still pays the full twenty.
/// </param>
/// <param name="Note">The sentence of the statute this band came from.</param>
/// <remarks>
/// A band may carry a flat amount, a rate, or both. Both is well defined — the
/// flat charge plus the rate on the portion — and no launch jurisdiction needs
/// it, but forbidding it would be a guess about the next statute.
/// </remarks>
public sealed record TaxBand(
    Money? UpTo,
    Percentage Rate,
    Money? Flat = null,
    string? Note = null);

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

    /// <summary>
    /// A graduated tax, lowest band first, where the jurisdiction levies one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty for the ordinary case of a flat percentage. Non-empty for a
    /// jurisdiction whose tax is not expressible as one rate — Oklahoma levies
    /// $20 on the first $1,500 of value plus 3.25% of the remainder
    /// (68 O.S. § 2103), which no single <see cref="Percentage"/> can represent.
    /// </para>
    /// <para>
    /// Bands and <see cref="StateRate"/> are mutually exclusive. A rule set
    /// carrying both does not describe one tax, and the engine refuses it rather
    /// than picking — see <see cref="BandError"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TaxBand> Bands { get; init; } = [];

    /// <summary>Whether the tax line rounds to the cent or to the dollar.</summary>
    public TaxRounding Rounding { get; init; } = TaxRounding.Cents;

    /// <summary>True when this jurisdiction's tax is graduated rather than flat.</summary>
    public bool IsBanded => Bands.Count > 0;

    /// <summary>The combined rate applied to the taxable amount.</summary>
    /// <remarks>
    /// Meaningless for a banded jurisdiction, where there is no single rate. Read
    /// <see cref="IsBanded"/> first; the engine does.
    /// </remarks>
    public Percentage CombinedRate => StateRate + LocalRate;

    /// <summary>
    /// Why these bands cannot be used, if they cannot.
    /// </summary>
    /// <remarks>
    /// Checked by the engine before it prices anything, and by the approval tool
    /// before a reviewer can sign. Every one of these would otherwise produce a
    /// confident, wrong number rather than a failure.
    /// </remarks>
    public string? BandError
    {
        get
        {
            if (!IsBanded)
            {
                return null;
            }

            if (!StateRate.IsZero || !LocalRate.IsZero)
            {
                return "This rule set carries both a flat rate and a banded rate. That does not "
                    + "describe one tax, and choosing between them would be a guess about money "
                    + "on a contract.";
            }

            for (var index = 0; index < Bands.Count - 1; index++)
            {
                if (Bands[index].UpTo is null)
                {
                    return $"Band {index + 1} of {Bands.Count} is unbounded but is not the last "
                        + "band, so the bands above it can never be reached.";
                }
            }

            if (Bands[^1].UpTo is not null)
            {
                return "The last band has an upper bound, so a vehicle valued above it would be "
                    + "taxed on only part of its value. The top band must be unbounded.";
            }

            for (var index = 1; index < Bands.Count; index++)
            {
                var previous = Bands[index - 1].UpTo;
                var current = Bands[index].UpTo;

                if (previous is not null && current is not null && current.Value <= previous.Value)
                {
                    return $"Band {index + 1} ends at {current} but band {index} already ends at "
                        + $"{previous}. Bands must ascend.";
                }
            }

            return null;
        }
    }

    /// <summary>True when <paramref name="date"/> falls inside this rule set's window.</summary>
    public bool CoversDate(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date < EffectiveTo.Value);

    /// <summary>Finds the rule for a fee code, if the jurisdiction names one.</summary>
    public FeeRule? FeeFor(string? code) => code is null
        ? null
        : Fees.FirstOrDefault(fee => string.Equals(fee.Code, code, StringComparison.OrdinalIgnoreCase));
}
