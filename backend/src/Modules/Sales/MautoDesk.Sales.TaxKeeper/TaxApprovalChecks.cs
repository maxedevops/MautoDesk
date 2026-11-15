using System.Globalization;
using MautoDesk.Sales.Application;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.TaxKeeper;

/// <summary>One precondition, and what it found.</summary>
/// <param name="Name">What was checked, in a few words.</param>
/// <param name="Passed">Whether it holds.</param>
/// <param name="Detail">What was actually seen, pass or fail.</param>
public sealed record Check(string Name, bool Passed, string Detail);

/// <summary>A draft tax rule set, as it stands in the database.</summary>
public sealed record TaxDraft
{
    /// <summary>The <c>sales.rule_set</c> row.</summary>
    public required Guid Id { get; init; }

    /// <summary>The taxing jurisdiction.</summary>
    public required Guid JurisdictionId { get; init; }

    /// <summary>The jurisdiction in words, for the operator to read.</summary>
    public required string JurisdictionName { get; init; }

    /// <summary>The shape of the <c>rules</c> jsonb.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The row's version number.</summary>
    public required int Version { get; init; }

    /// <summary>First date it governs.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Last date it governs, exclusive.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>Whether it is already approved.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>What it was read from.</summary>
    public required string SourceCitation { get; init; }

    /// <summary>The rules, verbatim.</summary>
    public required string RulesJson { get; init; }

    /// <summary>The drafter's note, carried onto the approved row.</summary>
    public string? Notes { get; init; }
}

/// <summary>What the operator is asking for.</summary>
public sealed record TaxApprovalRequest
{
    /// <summary>The draft rule set to approve.</summary>
    public required Guid DraftId { get; init; }

    /// <summary>The version number the approved row will carry.</summary>
    public required int Version { get; init; }

    /// <summary>The reviewer's real name, recorded in <c>approved_by</c>.</summary>
    public required string ReviewedBy { get; init; }

    /// <summary>Whoever is running the tool, recorded on the audit event.</summary>
    public required string OperatorName { get; init; }

    /// <summary>First date the approved rules govern.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>First date they no longer govern.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>The reviewer's written sign-off.</summary>
    public required string Note { get; init; }

    /// <summary>Where that sign-off was read from, recorded on the audit event.</summary>
    public required string NotePath { get; init; }

    /// <summary>Whether to actually write.</summary>
    public bool Commit { get; init; }
}

/// <summary>
/// The mechanical preconditions for approving a tax rule set.
/// </summary>
/// <remarks>
/// <para>
/// None of these is a tax judgement. Every one corresponds to a mistake that is
/// easy to make at a keyboard and expensive to find on a signed retail contract,
/// and a reviewer's sign-off cannot waive them — what they protect is the
/// ability to show afterwards what was approved and against what.
/// </para>
/// <para>
/// The band checks deliberately delegate to <see cref="TaxRuleSet.BandError"/>,
/// the same property the engine consults before it prices anything. One
/// definition of a coherent band set, not two that can drift.
/// </para>
/// </remarks>
public static class TaxApprovalChecks
{
    /// <summary>Fields that must not still be UNVERIFIED on an approved row.</summary>
    /// <remarks>
    /// Not the whole list from docs/16. These are the ones the engine reads to
    /// produce a figure: a rule set approved with any of them unverified prices a
    /// deal using a silent default, which is the failure this project exists to
    /// avoid. The remainder (title fees, tag fees, the sales tax alongside the
    /// excise tax) are absences a reviewer may legitimately approve around, and
    /// the register tracks them separately.
    /// </remarks>
    private static readonly string[] MustBeSettled =
    [
        "taxBasis",
        "tradeInCreditPolicy",
    ];

    /// <summary>Runs every check against one proposed approval.</summary>
    /// <param name="request">What the operator asked for.</param>
    /// <param name="draft">The draft as it stands.</param>
    /// <param name="approved">Existing approved rule sets for the jurisdiction.</param>
    public static IReadOnlyList<Check> Run(
        TaxApprovalRequest request,
        TaxDraft draft,
        IReadOnlyList<(int Version, DateOnly From, DateOnly? To)> approved)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(approved);

        return
        [
            IsADraft(draft),
            ReviewerIsNamed(request),
            VersionIsNew(request, draft, approved),
            PeriodIsAPeriod(request),
            SourceIsCited(draft),
            NothingLeftUnverified(draft),
            ItDeserializes(draft, request),
            BandsAreCoherent(draft, request),
            ItPricesSomething(draft, request),
            NoApprovedOverlap(request, approved),
        ];
    }

    private static Check IsADraft(TaxDraft draft) => new(
        "The draft is a draft",
        !draft.IsApproved,
        draft.IsApproved
            ? $"Rule set version {draft.Version} is already approved. An approval is a new row, "
              + "so approving an approved one means somebody has the wrong id."
            : $"Version {draft.Version} is unapproved, as a draft should be.");

    private static Check ReviewerIsNamed(TaxApprovalRequest request)
    {
        var name = request.ReviewedBy.Trim();

        var bad = name.Length < 3
            || name.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);

        return new(
            "The reviewer is named",
            !bad,
            bad
                ? $"'{request.ReviewedBy}' is not a person. An approval nobody owns is not an "
                  + "approval, and approved_by is the whole point of the column."
                : $"{name} will be recorded as having approved these figures.");
    }

    private static Check VersionIsNew(
        TaxApprovalRequest request,
        TaxDraft draft,
        IReadOnlyList<(int Version, DateOnly From, DateOnly? To)> approved)
    {
        if (request.Version == draft.Version)
        {
            return new(
                "The approved version is a new version",
                false,
                $"Version {request.Version} is the draft's own. sales.rule_set is append-only as "
                + "of V0017, and the unique index on (jurisdiction_id, version) would refuse this "
                + "anyway.");
        }

        var clash = approved.FirstOrDefault(row => row.Version == request.Version);

        return clash.Version == request.Version
            ? new(
                "The approved version is a new version",
                false,
                $"Version {request.Version} is already approved for this jurisdiction, effective "
                + $"from {clash.From:yyyy-MM-dd}.")
            : new(
                "The approved version is a new version",
                true,
                $"Version {request.Version} is free; the draft stays at {draft.Version} as the "
                + "record of what was proposed.");
    }

    private static Check PeriodIsAPeriod(TaxApprovalRequest request)
    {
        var ok = request.EffectiveTo is null || request.EffectiveTo.Value > request.EffectiveFrom;

        return new(
            "The effective period is a period",
            ok,
            ok
                ? request.EffectiveTo is { } to
                    ? $"{request.EffectiveFrom:yyyy-MM-dd} up to {to:yyyy-MM-dd}, exclusive."
                    : $"From {request.EffectiveFrom:yyyy-MM-dd}, open-ended."
                : $"effective_to {request.EffectiveTo:yyyy-MM-dd} is not after effective_from "
                  + $"{request.EffectiveFrom:yyyy-MM-dd}.");
    }

    private static Check SourceIsCited(TaxDraft draft)
    {
        var citation = draft.SourceCitation.Trim();

        var bad = citation.Length < 20
            || citation.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
            || citation.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase);

        return new(
            "The source is cited",
            !bad,
            bad
                ? $"The citation reads '{citation}'. A tax figure with no statute behind it is the "
                  + "thing this system refuses to ship; 'the calculator said so' is not a defence."
                : citation);
    }

    private static Check NothingLeftUnverified(TaxDraft draft)
    {
        var unsettled = MustBeSettled
            .Where(field => draft.RulesJson.Contains(
                $"\"{field}\": \"UNVERIFIED\"", StringComparison.OrdinalIgnoreCase)
                || draft.RulesJson.Contains(
                    $"\"{field}\":\"UNVERIFIED\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new(
            "The figures the engine reads are settled",
            unsettled.Count == 0,
            unsettled.Count == 0
                ? "taxBasis and tradeInCreditPolicy both carry real values."
                : $"Still UNVERIFIED: {string.Join(", ", unsettled)}. The engine reads these to "
                  + "produce a figure, so approving them unverified prices a deal on a silent "
                  + "default. Whether a trade reduces the taxable amount alone can be worth "
                  + "thousands on one deal.");
    }

    private static Check ItDeserializes(TaxDraft draft, TaxApprovalRequest request)
    {
        var result = Deserialize(draft, request);

        return result.Error is { } error
            ? new("The rules deserialize as approved", false, $"{error.Code}: {error.Message}")
            : new(
                "The rules deserialize as approved",
                true,
                "The serializer reads every field it needs under its strict, approved-row rules.");
    }

    private static Check BandsAreCoherent(TaxDraft draft, TaxApprovalRequest request)
    {
        var result = Deserialize(draft, request);

        if (result.IsFailure)
        {
            return new(
                "The bands are coherent",
                false,
                "Not checked: the rules did not deserialize.");
        }

        var rules = result.Value;

        if (!rules.IsBanded)
        {
            return new(
                "The bands are coherent",
                true,
                $"Not a banded jurisdiction; a flat {rules.CombinedRate}% applies.");
        }

        return rules.BandError is { } error
            ? new("The bands are coherent", false, error)
            : new(
                "The bands are coherent",
                true,
                $"{rules.Bands.Count} ascending bands, the last unbounded, rounding to the "
                + $"{(rules.Rounding == TaxRounding.Dollars ? "dollar" : "cent")}.");
    }

    /// <summary>
    /// A rule set that computes nothing on a plain retail deal is not approvable.
    /// </summary>
    /// <remarks>
    /// The cheapest possible end-to-end proof. A rule set can satisfy every other
    /// check and still produce zero tax — a band list whose rates are all zero, a
    /// flat rate left at its default. Zero tax on a $20,000 car is not a rounding
    /// error; it is a figure the dealer owes the state out of their own pocket.
    /// </remarks>
    private static Check ItPricesSomething(TaxDraft draft, TaxApprovalRequest request)
    {
        var result = Deserialize(draft, request);

        if (result.IsFailure)
        {
            return new(
                "It prices a real deal",
                false,
                "Not checked: the rules did not deserialize.");
        }

        var priced = DealEngine.Calculate(
            new DealInput
            {
                DealId = Guid.Empty,
                DealDate = request.EffectiveFrom,
                VehiclePrice = Money.FromDecimal(20_000m),
            },
            result.Value);

        if (priced.Error is { } refusal)
        {
            return new(
                "It prices a real deal",
                false,
                $"The engine refused a $20,000 cash deal: {refusal.Code} — {refusal.Message}");
        }

        var tax = priced.Value.SalesTax;

        return tax.IsZero
            ? new(
                "It prices a real deal",
                false,
                "A $20,000 cash deal came out with no tax at all. Every other check can pass on "
                + "a rule set whose rates are all zero, and nothing downstream would question a "
                + "confident $0.00.")
            : new(
                "It prices a real deal",
                true,
                $"A $20,000 cash deal is taxed {tax} on a basis of {priced.Value.TaxableAmount}.");
    }

    private static Check NoApprovedOverlap(
        TaxApprovalRequest request,
        IReadOnlyList<(int Version, DateOnly From, DateOnly? To)> approved)
    {
        var from = request.EffectiveFrom;
        var to = request.EffectiveTo;

        var clash = approved.FirstOrDefault(row =>
            from < (row.To ?? DateOnly.MaxValue) && (to ?? DateOnly.MaxValue) > row.From);

        var overlaps = clash != default;

        return new(
            "No approved rule set covers these dates",
            !overlaps,
            overlaps
                ? $"Version {clash.Version} already governs {clash.From:yyyy-MM-dd} to "
                  + $"{(clash.To is { } end ? end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "open-ended")}. Two "
                  + "approved rule sets covering one day means two possible prices for one deal, "
                  + "and rule_set_no_overlap would refuse the insert anyway."
                : approved.Count == 0
                    ? "Nothing is approved for this jurisdiction yet; this would be the first."
                    : $"{approved.Count} approved rule set(s), none covering these dates.");
    }

    private static Result<TaxRuleSet> Deserialize(TaxDraft draft, TaxApprovalRequest request) =>
        TaxRuleSetSerializer.Deserialize(new TaxRuleSetRow(
            draft.Id,
            draft.JurisdictionId,
            request.Version,
            request.EffectiveFrom,
            request.EffectiveTo,

            // Deserialized as though already approved, which is the point: the
            // serializer is lenient on an unapproved row and strict on an approved
            // one, and what is being checked here is whether it survives the strict
            // reading it is about to be subjected to for real.
            IsApproved: true,
            draft.SourceCitation,
            draft.RulesJson));
}
