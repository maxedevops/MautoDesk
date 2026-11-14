using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MautoDesk.Compliance.Domain;

/// <summary>The verdict on one rule.</summary>
/// <param name="RuleCode">The rule's stable code.</param>
/// <param name="RuleVersion">Which version was applied.</param>
/// <param name="RuleVersionId">The row it came from.</param>
/// <param name="SourceVersionId">The archived source text behind it.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Severity">How seriously a firing is taken.</param>
/// <param name="Message">What a human reads.</param>
/// <param name="RequirementCode">The requirement that is outstanding, where one is.</param>
/// <param name="Deadline">The date this must be done by, where the rule sets one.</param>
/// <param name="EvaluationDetail">Which conditions were compared, and to what.</param>
public sealed record ComplianceResult(
    string RuleCode,
    string RuleVersion,
    Guid RuleVersionId,
    Guid SourceVersionId,
    ComplianceStatus Status,
    RuleSeverity Severity,
    string Message,
    string? RequirementCode = null,
    DateOnly? Deadline = null,
    IReadOnlyList<string>? EvaluationDetail = null);

/// <summary>Everything one evaluation produced.</summary>
/// <param name="TriggerEvent">Which lifecycle event was evaluated.</param>
/// <param name="JurisdictionCode">Whose law was applied.</param>
/// <param name="EvaluatedOn">The date the rules were resolved as of.</param>
/// <param name="EngineVersion">The evaluator that produced this.</param>
/// <param name="RulePackVersion">The resolved pack, distinct from any one rule.</param>
/// <param name="OverallStatus">Green, yellow or red.</param>
/// <param name="Results">Every rule's verdict, in a stable order.</param>
/// <param name="InputHash">SHA-256 of the canonical facts.</param>
/// <param name="ResultHash">SHA-256 of the canonical results.</param>
public sealed record ComplianceRun(
    string TriggerEvent,
    string JurisdictionCode,
    DateOnly EvaluatedOn,
    string EngineVersion,
    string RulePackVersion,
    DealReadiness OverallStatus,
    IReadOnlyList<ComplianceResult> Results,
    byte[] InputHash,
    byte[] ResultHash)
{
    /// <summary>Rules that fired, block, and are not satisfied.</summary>
    public IReadOnlyList<ComplianceResult> Blockers =>
        [.. Results.Where(result => result.Status == ComplianceStatus.Block)];

    /// <summary>How many rules are blocking.</summary>
    public int BlockingCount => Results.Count(result => result.Status == ComplianceStatus.Block);

    /// <summary>How many rules warn or need review.</summary>
    public int WarningCount => Results.Count(result =>
        result.Status is ComplianceStatus.Warning or ComplianceStatus.Review);
}

/// <summary>The user-facing readiness light.</summary>
/// <remarks>
/// Deliberately three states and not a score. A manager glancing at a desk needs
/// "can this be delivered" answered, and a number between 0 and 100 does not
/// answer it.
/// </remarks>
public enum DealReadiness
{
    /// <summary>Nothing outstanding.</summary>
    Green,

    /// <summary>Something wants attention, nothing prevents the deal.</summary>
    Yellow,

    /// <summary>At least one blocking rule is unsatisfied.</summary>
    Red,
}

/// <summary>
/// Evaluates a deal against the rules in force on its date.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and deterministic.</b> Same facts, same rules, same engine version →
/// same results and the same result hash, on any machine, in any locale, forever.
/// That is what makes "which rules applied to this deal on 19 August 2026" a
/// question with an answer rather than an opinion.
/// </para>
/// <para>
/// <b>It decides nothing about the law.</b> Every verdict traces to a rule version
/// with an approver and a cited source version. The evaluator's whole job is to
/// apply what a human already approved — which is why AI is nowhere near it, and
/// why an AI explanation is required to quote a <see cref="ComplianceResult"/>
/// rather than reason about a regulation.
/// </para>
/// </remarks>
public static class RuleEvaluator
{
    /// <summary>
    /// The evaluator's version, stamped onto every run.
    /// </summary>
    /// <remarks>
    /// Bump when a change alters a verdict for facts that previously produced a
    /// different one. A run records the version that produced it, so a
    /// reproduction pins the logic of the day.
    /// </remarks>
    public const string Version = "1.0.0";

    /// <summary>Evaluates every applicable rule and produces a run.</summary>
    /// <param name="request">What to evaluate, and against what.</param>
    public static ComplianceRun Evaluate(EvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var applicable = Resolve(request);
        var results = new List<ComplianceResult>();

        foreach (var rule in applicable)
        {
            results.AddRange(EvaluateRule(rule, request));
        }

        // Sorted by rule code, then version. Without a stable order the result hash
        // would depend on however the database happened to return the rules, and
        // two identical evaluations would disagree about their own reproducibility.
        results.Sort(static (left, right) =>
        {
            var byCode = string.CompareOrdinal(left.RuleCode, right.RuleCode);
            return byCode != 0 ? byCode : string.CompareOrdinal(left.RuleVersion, right.RuleVersion);
        });

        var readiness = Readiness(results);

        return new ComplianceRun(
            request.TriggerEvent,
            request.JurisdictionCode,
            request.EvaluatedOn,
            Version,
            request.RulePackVersion,
            readiness,
            results,
            request.Context.Hash(),
            HashResults(results, readiness));
    }

    /// <summary>
    /// Picks the rules that govern this deal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jurisdiction, trigger event, and the date the deal was written. Approval and
    /// lifecycle status are checked inside
    /// <see cref="RegulatoryRuleVersion.GovernsOn"/>, so an unapproved draft cannot
    /// reach the evaluator however it was loaded.
    /// </para>
    /// <para>
    /// At most one version of a rule can match, because
    /// <c>regulatory_rule_version_no_overlap</c> forbids two approved versions
    /// covering the same day. If that constraint were ever dropped, this would
    /// silently start evaluating both — so the guard below turns that into a loud
    /// failure instead.
    /// </para>
    /// </remarks>
    private static List<RegulatoryRuleVersion> Resolve(EvaluationRequest request)
    {
        var applicable = request.Rules
            .Where(rule => string.Equals(
                rule.JurisdictionCode, request.JurisdictionCode, StringComparison.OrdinalIgnoreCase))
            .Where(rule => string.Equals(
                rule.TriggerEvent, request.TriggerEvent, StringComparison.OrdinalIgnoreCase))
            .Where(rule => rule.GovernsOn(request.EvaluatedOn))
            .ToList();

        var ambiguous = applicable
            .GroupBy(rule => rule.RuleCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (ambiguous is not null)
        {
            throw new InvalidOperationException(
                $"Two approved versions of {ambiguous.Key} both cover {request.EvaluatedOn:yyyy-MM-dd} " +
                $"({string.Join(", ", ambiguous.Select(rule => rule.Version))}). The engine cannot " +
                "choose between them, and guessing would mean two identical deals could be judged " +
                "differently. The exclusion constraint on compliance.regulatory_rule_version is " +
                "supposed to make this impossible.");
        }

        return applicable;
    }

    /// <summary>Evaluates one rule into one or more results.</summary>
    private static IEnumerable<ComplianceResult> EvaluateRule(
        RegulatoryRuleVersion rule,
        EvaluationRequest request)
    {
        var trace = new List<string>();
        var fired = rule.Conditions.Matches(request.Context, trace);

        if (!fired)
        {
            // The rule did not apply. Recorded as a pass rather than omitted,
            // because "we considered this rule and it did not apply" is a
            // materially different claim from "we never looked", and an auditor is
            // entitled to the difference.
            yield return new ComplianceResult(
                rule.RuleCode, rule.Version, rule.Id, rule.SourceVersionId,
                ComplianceStatus.Pass, rule.Severity,
                "Does not apply to this deal.", null, null, trace);

            yield break;
        }

        var deadline = ResolveDeadline(rule, request);
        var requirements = rule.Actions.OfType<RequireAction>().ToList();
        var prohibitions = rule.Actions.OfType<ProhibitAction>().ToList();

        foreach (var prohibition in prohibitions)
        {
            // Nothing to satisfy: the deal is structured in a way the rule forbids.
            yield return new ComplianceResult(
                rule.RuleCode, rule.Version, rule.Id, rule.SourceVersionId,
                StatusFor(rule.Severity), rule.Severity,
                prohibition.Message, null, deadline, trace);
        }

        foreach (var requirement in requirements)
        {
            var satisfied = request.Context.IsSatisfied(requirement.Code);

            yield return new ComplianceResult(
                rule.RuleCode, rule.Version, rule.Id, rule.SourceVersionId,
                satisfied ? ComplianceStatus.Pass : StatusFor(rule.Severity),
                rule.Severity,
                satisfied ? $"{requirement.Name} is on file." : $"{requirement.Name} is required.",
                requirement.Code,
                satisfied ? null : deadline,
                trace);
        }

        // A rule that fires with no actions is a bare assertion — it fired, and the
        // firing is the finding. Common for review-severity rules that exist to put
        // a deal in front of a human.
        if (requirements.Count == 0 && prohibitions.Count == 0)
        {
            yield return new ComplianceResult(
                rule.RuleCode, rule.Version, rule.Id, rule.SourceVersionId,
                StatusFor(rule.Severity), rule.Severity,
                string.IsNullOrWhiteSpace(rule.Message) ? "Applies to this deal." : rule.Message,
                null, deadline, trace);
        }
    }

    private static DateOnly? ResolveDeadline(RegulatoryRuleVersion rule, EvaluationRequest request)
    {
        var action = rule.Actions.OfType<DeadlineAction>().FirstOrDefault();

        if (action is null)
        {
            return null;
        }

        if (request.Calendar is null)
        {
            // Refusing beats guessing. A deadline computed without a holiday
            // calendar is wrong on every holiday, and wrong quietly.
            throw new InvalidOperationException(
                $"{rule.RuleCode} sets a {action.Days}-day deadline, but no business-day calendar " +
                $"was supplied for {request.JurisdictionCode}. Load one before evaluating rules " +
                "that carry deadlines.");
        }

        return request.Calendar.Apply(action, request.TriggerDate ?? request.EvaluatedOn);
    }

    private static ComplianceStatus StatusFor(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Blocking => ComplianceStatus.Block,
        RuleSeverity.Warning => ComplianceStatus.Warning,
        RuleSeverity.Review => ComplianceStatus.Review,
        _ => ComplianceStatus.Pass,
    };

    private static DealReadiness Readiness(IReadOnlyList<ComplianceResult> results)
    {
        if (results.Any(result => result.Status == ComplianceStatus.Block))
        {
            return DealReadiness.Red;
        }

        return results.Any(result =>
            result.Status is ComplianceStatus.Warning or ComplianceStatus.Review)
            ? DealReadiness.Yellow
            : DealReadiness.Green;
    }

    /// <summary>
    /// Hashes the verdicts, for <c>compliance_run.result_hash</c>.
    /// </summary>
    /// <remarks>
    /// Covers rule code, version, status and requirement — the things that decide
    /// what happens to the deal. Deliberately not the message or the evaluation
    /// trace: rewording a message for clarity must not make it look as though the
    /// compliance outcome changed.
    /// </remarks>
    private static byte[] HashResults(IReadOnlyList<ComplianceResult> results, DealReadiness readiness)
    {
        var builder = new StringBuilder();
        builder.Append(readiness).Append('\n');

        foreach (var result in results)
        {
            builder
                .Append(result.RuleCode).Append('|')
                .Append(result.RuleVersion).Append('|')
                .Append(result.Status.ToString()).Append('|')
                .Append(result.RequirementCode ?? "-").Append('|')
                .Append(result.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-")
                .Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}

/// <summary>What the evaluator needs for one run.</summary>
public sealed record EvaluationRequest
{
    /// <summary>The lifecycle event being evaluated, e.g. <c>DEAL_SOLD</c>.</summary>
    public required string TriggerEvent { get; init; }

    /// <summary>Whose law applies, e.g. <c>US-OK</c>.</summary>
    public required string JurisdictionCode { get; init; }

    /// <summary>
    /// The date the rules are resolved as of.
    /// </summary>
    /// <remarks>
    /// The date the deal was written, not today. Re-evaluating a January deal in
    /// July must apply January's rules, or a rule change would retroactively make a
    /// compliant deal non-compliant.
    /// </remarks>
    public required DateOnly EvaluatedOn { get; init; }

    /// <summary>The date a deadline counts from, where it differs from the evaluation date.</summary>
    public DateOnly? TriggerDate { get; init; }

    /// <summary>The resolved pack version, recorded on the run.</summary>
    public required string RulePackVersion { get; init; }

    /// <summary>The facts.</summary>
    public required RegulatoryContext Context { get; init; }

    /// <summary>Candidate rules, which the evaluator filters down to the applicable ones.</summary>
    public required IReadOnlyList<RegulatoryRuleVersion> Rules { get; init; }

    /// <summary>The holiday calendar, needed only when a rule carries a deadline.</summary>
    public BusinessDayCalendar? Calendar { get; init; }
}
