namespace MautoDesk.Compliance.Contracts;

/// <summary>
/// The Compliance module's public surface.
/// </summary>
/// <remarks>
/// The only project in the Compliance module another module may reference — which
/// matters more here than elsewhere, because Deal Guardian will orchestrate over
/// this module and the temptation to reach into the evaluator directly will be
/// constant.
/// </remarks>
public static class CompliancePermissions
{
    /// <summary>See compliance results and the Deal Guardian panel.</summary>
    public const string EvaluationRead = "compliance.evaluation.read";

    /// <summary>Ask for a blocking rule to be overridden.</summary>
    public const string OverrideRequest = "compliance.override.request";

    /// <summary>Approve or reject an override request.</summary>
    /// <remarks>
    /// Sensitive, and never held by the same person who requested. An override
    /// approved by its own requester is not an approval.
    /// </remarks>
    public const string OverrideApprove = "compliance.override.approve";

    /// <summary>Read regulatory rules, versions and sources.</summary>
    public const string RuleRead = "compliance.rule.read";
}

/// <summary>One rule's verdict, as the API returns it.</summary>
public sealed record ComplianceResultDto(
    string RuleCode,
    string RuleVersion,
    string Status,
    string Severity,
    string Message,
    string? RequirementCode,
    DateOnly? Deadline,
    string? SourceCitation,
    IReadOnlyList<string> EvaluationDetail);

/// <summary>
/// A whole evaluation, as the Deal Guardian panel renders it.
/// </summary>
/// <remarks>
/// <see cref="InputHash"/> and <see cref="ResultHash"/> are hex, and they are on
/// the wire deliberately. "This deal was evaluated against exactly these facts and
/// produced exactly this outcome" is a claim a dealer may one day need to make to
/// somebody who is not inclined to take their word for it.
/// </remarks>
public sealed record ComplianceRunDto(
    Guid Id,
    Guid DealId,
    string TriggerEvent,
    string JurisdictionCode,
    DateOnly EvaluatedOn,
    string EngineVersion,
    string RulePackVersion,
    string OverallStatus,
    int BlockingCount,
    int WarningCount,
    DateTimeOffset ExecutedAt,
    IReadOnlyList<ComplianceResultDto> Results,
    string InputHash,
    string ResultHash);

/// <summary>A document the rules say this deal needs.</summary>
public sealed record DocumentRequirementDto(
    string Code,
    string Name,
    string Status,
    bool IsBlocking,
    string? RuleCode,
    string? RuleVersion,
    Guid? SatisfiedDocumentId);
