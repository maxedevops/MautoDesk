using System.Security.Cryptography;
using MautoDesk.Compliance.Application;
using MautoDesk.Compliance.Domain;

namespace MautoDesk.Compliance.RuleKeeper;

/// <summary>One thing that was checked, and what it found.</summary>
public sealed record Check(string Name, bool Passed, string Detail);

/// <summary>The draft as the database holds it, plus what it needs to be judged.</summary>
public sealed record DraftVersion
{
    /// <summary>The draft row's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The rule this is a version of.</summary>
    public required Guid RuleId { get; init; }

    /// <summary>e.g. <c>OK-TEMP-TAG-FLEET-001</c>.</summary>
    public required string RuleCode { get; init; }

    /// <summary>e.g. <c>US-OK</c>.</summary>
    public required string JurisdictionCode { get; init; }

    /// <summary>The draft's own version string.</summary>
    public required string Version { get; init; }

    /// <summary>Whether it is already approved. It must not be.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>e.g. <c>DEAL_SOLD</c>.</summary>
    public required string TriggerEvent { get; init; }

    /// <summary>blocking, warning, review or informational.</summary>
    public required string Severity { get; init; }

    /// <summary>The raw rule definition json.</summary>
    public required string DefinitionJson { get; init; }

    /// <summary>The source version it was written against.</summary>
    public required Guid SourceVersionId { get; init; }

    /// <summary>Where the archived document lives, relative to the repository.</summary>
    public string? SourceLocation { get; init; }

    /// <summary>The sha256 recorded for that document.</summary>
    public byte[]? SourceHash { get; init; }

    /// <summary>The source's citation, for the operator to read back.</summary>
    public required string SourceCitation { get; init; }

    /// <summary>The draft's own approval note, which carries its flagged readings.</summary>
    public string? ApprovalNote { get; init; }
}

/// <summary>One stored test case for the draft.</summary>
public sealed record TestCase(string Name, string Expectation, string InputJson);

/// <summary>
/// Everything that must be true before a rule may govern a real deal.
/// </summary>
/// <remarks>
/// <para>
/// These checks are the reason this tool exists rather than a <c>psql</c>
/// one-liner. Each one encodes something the project already decided: that an
/// approval names a reviewer, that it cites an archived document, that a rule
/// with only a firing case is a constant rather than a rule, that the engine
/// never guesses a business-day deadline. An operator with database access could
/// insert the row without any of it — and the point of the tool is that nobody
/// has to remember the list.
/// </para>
/// <para>
/// Nothing here is a legal judgement. Every check is mechanical. Whether the
/// reading is <em>right</em> is the reviewer's work, and no amount of validation
/// substitutes for it.
/// </para>
/// </remarks>
public static class ApprovalChecks
{
    private static readonly string[] PlaceholderTokens =
        ["UNVERIFIED", "TODO", "PLACEHOLDER", "TBD", "FIXME", "XXX"];

    /// <summary>Runs every check that does not need the database.</summary>
    public static IReadOnlyList<Check> Run(
        ApprovalRequest request,
        DraftVersion draft,
        IReadOnlyList<TestCase> tests,
        IReadOnlyList<(DateOnly From, DateOnly? To, string Version)> approved,
        IReadOnlySet<int> holidayYears)
    {
        var checks = new List<Check>
        {
            NotAlreadyApproved(draft),
            ReviewerIsNamed(request),
            VersionIsNew(request, draft, approved),
            DatesAreSane(request),
            SourceIsArchived(draft),
            DefinitionParses(draft),
            CorpusProvesSomething(tests),
            NoApprovedOverlap(request, approved),
        };

        var definition = RuleDefinitionSerializer.Parse(draft.RuleCode, draft.DefinitionJson);

        if (definition.IsSuccess)
        {
            checks.Add(EveryCaseAgrees(draft, definition.Value!, tests));
            checks.Add(DeadlinesHaveACalendar(request, definition.Value!, holidayYears));
        }

        if (request.RepositoryRoot is not null)
        {
            checks.Add(ArchivedSourceStillHashes(draft, request.RepositoryRoot));
        }

        return checks;
    }

    private static Check NotAlreadyApproved(DraftVersion draft) =>
        new("The draft is a draft",
            !draft.IsApproved,
            draft.IsApproved
                ? $"{draft.RuleCode} {draft.Version} is already approved. An approval is a new row; "
                  + "approve the draft you mean, or add a new draft first."
                : $"{draft.RuleCode} {draft.Version} is unapproved, as a draft should be.");

    private static Check ReviewerIsNamed(ApprovalRequest request)
    {
        var name = request.ReviewedBy;
        var upper = name.ToUpperInvariant();
        var placeholder = PlaceholderTokens.FirstOrDefault(token =>
            upper.Contains(token, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(name))
        {
            return new("The reviewer is named", false, "--reviewed-by is empty.");
        }

        if (placeholder is not null)
        {
            return new("The reviewer is named", false,
                $"'{name}' contains '{placeholder}'. rule_pack_approval.sql rejects this in CI, and "
                + "it would be rejected for the right reason: an approval nobody owns is not an "
                + "approval.");
        }

        return name.Length < 3
            ? new("The reviewer is named", false, $"'{name}' is too short to be a person's name.")
            : new("The reviewer is named", true, $"approved_by will record '{name}'.");
    }

    private static Check VersionIsNew(
        ApprovalRequest request,
        DraftVersion draft,
        IReadOnlyList<(DateOnly From, DateOnly? To, string Version)> approved)
    {
        if (string.Equals(request.Version, draft.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new("The approved version is a new version", false,
                $"--version '{request.Version}' is the draft's own version. The approved row is a "
                + "separate row and needs its own version, e.g. 1.0.0.");
        }

        var clash = approved.FirstOrDefault(a =>
            string.Equals(a.Version, request.Version, StringComparison.OrdinalIgnoreCase));

        return clash.Version is not null
            ? new("The approved version is a new version", false,
                $"version '{request.Version}' already exists on this rule.")
            : new("The approved version is a new version", true,
                $"'{request.Version}' is unused on {draft.RuleCode}.");
    }

    private static Check DatesAreSane(ApprovalRequest request)
    {
        if (request.EffectiveTo is { } to && to <= request.EffectiveFrom)
        {
            return new("The effective period is a period", false,
                $"--effective-to {to:yyyy-MM-dd} is not after --effective-from "
                + $"{request.EffectiveFrom:yyyy-MM-dd}. The range is half-open: 'to' is the first "
                + "day the rule no longer governs.");
        }

        return new("The effective period is a period", true,
            request.EffectiveTo is { } until
                ? $"governs {request.EffectiveFrom:yyyy-MM-dd} up to but not including {until:yyyy-MM-dd}."
                : $"governs from {request.EffectiveFrom:yyyy-MM-dd}, open-ended.");
    }

    /// <summary>
    /// The requested period does not collide with an already-approved one.
    /// </summary>
    /// <remarks>
    /// The exclusion constraint on the table is the real guarantee; this check
    /// exists so the refusal arrives as a sentence rather than as a constraint
    /// name, and before the operator has committed anything.
    /// </remarks>
    private static Check NoApprovedOverlap(
        ApprovalRequest request,
        IReadOnlyList<(DateOnly From, DateOnly? To, string Version)> approved)
    {
        var from = request.EffectiveFrom;
        var to = request.EffectiveTo ?? DateOnly.MaxValue;

        var clashes = approved
            .Where(existing => from < (existing.To ?? DateOnly.MaxValue) && existing.From < to)
            .Select(existing => existing.To is { } until
                ? $"{existing.Version} ({existing.From:yyyy-MM-dd} to {until:yyyy-MM-dd})"
                : $"{existing.Version} (from {existing.From:yyyy-MM-dd}, open-ended)")
            .ToList();

        if (clashes.Count == 0)
        {
            return new("No approved version covers these dates", true,
                approved.Count == 0
                    ? "this would be the rule's first approved version."
                    : $"{approved.Count} approved version(s) exist and none overlap.");
        }

        return new("No approved version covers these dates", false,
            $"these dates overlap {string.Join(", ", clashes)}. Two approved versions of one "
            + "rule may not cover the same day, because the resolver could then legitimately "
            + "pick either and two identical deals could get different outcomes.");
    }

    private static Check SourceIsArchived(DraftVersion draft) =>
        draft.SourceLocation is null
            ? new("The source is archived", false,
                $"the source version cited by {draft.RuleCode} has no content_location, so "
                + "\"this rule was written against this text\" cannot be checked. Approving "
                + "against a URL is not the same as reading the law.")
            : new("The source is archived", true, $"{draft.SourceLocation} — {draft.SourceCitation}");

    private static Check DefinitionParses(DraftVersion draft)
    {
        var parsed = RuleDefinitionSerializer.Parse(draft.RuleCode, draft.DefinitionJson);

        return parsed.IsSuccess
            ? new("The definition parses", true, "the evaluator can read this rule.")
            : new("The definition parses", false,
                $"the evaluator cannot read this rule, so approving it would activate something "
                + $"that never fires: {parsed.Error?.Message}");
    }

    private static Check CorpusProvesSomething(IReadOnlyList<TestCase> tests)
    {
        if (tests.Count == 0)
        {
            return new("The test corpus proves something", false,
                "the draft has no test cases. A rule nobody tested is a rule nobody has seen work.");
        }

        var firing = tests.Count(t => t.Expectation is not "pass");
        var passing = tests.Count(t => t.Expectation is "pass");

        if (firing == 0)
        {
            return new("The test corpus proves something", false,
                $"{tests.Count} case(s), none of which expect the rule to fire. This proves the "
                + "rule is quiet, not that it works.");
        }

        return passing == 0
            ? new("The test corpus proves something", false,
                $"{tests.Count} case(s), all of which expect the rule to fire. A rule with only a "
                + "firing case has been shown to be a constant, not a rule.")
            : new("The test corpus proves something", true,
                $"{tests.Count} case(s): {firing} where it fires, {passing} where it does not.");
    }

    private static Check EveryCaseAgrees(
        DraftVersion draft,
        RuleDefinitionSerializer.Definition definition,
        IReadOnlyList<TestCase> tests)
    {
        var rule = new RegulatoryRuleVersion
        {
            Id = draft.Id,
            RuleCode = draft.RuleCode,
            Version = draft.Version,
            JurisdictionCode = draft.JurisdictionCode,
            TriggerEvent = draft.TriggerEvent,
            Severity = Enum.Parse<RuleSeverity>(draft.Severity, ignoreCase: true),
            // The evaluator ignores unapproved rules, which is the correct
            // production behaviour and would make every case here vacuously
            // pass. Approving the in-memory copy tests the rule as it will
            // behave once the row is written; the database is untouched.
            Status = RuleStatus.Active,
            // Any date in the past: the cases are about whether the rule fires on
            // the facts, not about when it governs. The real effective dates are
            // checked separately, because getting them wrong is a different
            // mistake from getting the logic wrong.
            EffectiveFrom = new DateOnly(1900, 1, 1),
            IsApproved = true,
            SourceVersionId = draft.SourceVersionId,
            Conditions = definition.Conditions,
            Actions = definition.Actions,
        };

        var mismatches = new List<string>();

        foreach (var test in tests)
        {
            var expected = test.Expectation switch
            {
                "block" => ComplianceStatus.Block,
                "warning" => ComplianceStatus.Warning,
                "review" => ComplianceStatus.Review,
                _ => ComplianceStatus.Pass,
            };

            var actual = RuleCaseRunner.Worst(rule, test.InputJson);

            if (actual != expected)
            {
                mismatches.Add($"'{test.Name}' expected {test.Expectation}, got {actual}");
            }
        }

        return mismatches.Count == 0
            ? new("Every stored case agrees", true,
                $"{tests.Count} case(s) evaluated, {tests.Count} agreed.")
            : new("Every stored case agrees", false,
                $"{mismatches.Count} of {tests.Count} case(s) disagree with the rule as written: "
                + string.Join("; ", mismatches));
    }

    private static Check DeadlinesHaveACalendar(
        ApprovalRequest request,
        RuleDefinitionSerializer.Definition definition,
        IReadOnlySet<int> holidayYears)
    {
        var businessDay = definition.Actions
            .OfType<DeadlineAction>()
            .Where(deadline => deadline.BusinessDays)
            .ToList();

        if (businessDay.Count == 0)
        {
            return new("Business-day deadlines have a calendar", true,
                "the rule carries no business-day deadline.");
        }

        var years = new List<int>();

        for (var year = request.EffectiveFrom.Year;
             year <= (request.EffectiveTo?.Year ?? request.EffectiveFrom.Year);
             year++)
        {
            years.Add(year);
        }

        var missing = years.Where(year => !holidayYears.Contains(year)).ToList();

        return missing.Count == 0
            ? new("Business-day deadlines have a calendar", true,
                $"{string.Join(", ", years)} covered by compliance.jurisdiction_holiday.")
            : new("Business-day deadlines have a calendar", false,
                $"the rule counts in business days but no holidays are loaded for "
                + $"{string.Join(", ", missing)}. The engine refuses to compute a deadline without a "
                + "calendar, so approving this would activate a rule that cannot run.");
    }

    private static Check ArchivedSourceStillHashes(DraftVersion draft, string repositoryRoot)
    {
        if (draft.SourceLocation is null || draft.SourceHash is null)
        {
            return new("The archived source still hashes", false,
                "there is no archived document to re-hash.");
        }

        var path = Path.Combine(repositoryRoot, draft.SourceLocation.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return new("The archived source still hashes", false,
                $"{draft.SourceLocation} is recorded as archived but is not in the repository at "
                + $"{path}.");
        }

        var actual = SHA256.HashData(File.ReadAllBytes(path));

        return actual.AsSpan().SequenceEqual(draft.SourceHash)
            ? new("The archived source still hashes", true,
                $"{draft.SourceLocation} matches the recorded sha256.")
            : new("The archived source still hashes", false,
                $"{draft.SourceLocation} does not hash to what the database recorded. Either the "
                + "archived text was edited after the rule was drafted, or this checkout converted "
                + "its line endings. Either way the rule is no longer provably written against the "
                + "text being approved.");
    }
}
