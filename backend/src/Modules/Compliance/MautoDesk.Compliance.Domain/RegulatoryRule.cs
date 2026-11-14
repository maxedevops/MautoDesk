namespace MautoDesk.Compliance.Domain;

/// <summary>How seriously a fired rule is taken.</summary>
/// <remarks>Mirrors the check constraint on <c>compliance.regulatory_rule_version.severity</c>.</remarks>
public enum RuleSeverity
{
    /// <summary>The deal cannot proceed until this is resolved.</summary>
    Blocking,

    /// <summary>Proceed, but somebody should know.</summary>
    Warning,

    /// <summary>A human has to look at this and decide.</summary>
    Review,

    /// <summary>Worth recording, not worth interrupting anyone over.</summary>
    Informational,
}

/// <summary>The verdict on one rule for one deal.</summary>
/// <remarks>Mirrors the check constraint on <c>compliance.compliance_result.status</c>.</remarks>
public enum ComplianceStatus
{
    /// <summary>Either the rule did not apply, or it applied and was satisfied.</summary>
    Pass,

    /// <summary>Applied, unsatisfied, and not blocking.</summary>
    Warning,

    /// <summary>Applied, unsatisfied, and blocking.</summary>
    Block,

    /// <summary>Applied, and needs a human decision.</summary>
    Review,
}

/// <summary>The lifecycle state of a rule version.</summary>
/// <remarks>
/// Mirrors <c>compliance.regulatory_rule_version.status</c>. Only
/// <see cref="Active"/> and <see cref="Scheduled"/> rows the resolver finds
/// in-window are evaluated, and only if they are also approved — a draft is
/// invisible to the evaluator no matter what its dates say.
/// </remarks>
public enum RuleStatus
{
    /// <summary>Being written.</summary>
    Draft,

    /// <summary>Awaiting a reviewer.</summary>
    Review,

    /// <summary>Approved, not yet in force.</summary>
    Approved,

    /// <summary>Approved with a future effective date.</summary>
    Scheduled,

    /// <summary>In force.</summary>
    Active,

    /// <summary>Superseded or withdrawn.</summary>
    Retired,
}

/// <summary>What a rule does when it fires.</summary>
public abstract record RuleAction;

/// <summary>
/// The deal needs something before it may proceed.
/// </summary>
/// <remarks>
/// The commonest shape by far. The rule's conditions decide whether the
/// requirement <em>applies</em>; whether it is <em>met</em> is a separate fact,
/// supplied on the context. That separation is why one rule can say "a financed
/// deal needs a security agreement" without also having to know how documents are
/// stored.
/// </remarks>
/// <param name="Code">A stable requirement code, e.g. <c>BUYERS_GUIDE</c>.</param>
/// <param name="Name">What a salesperson reads.</param>
/// <param name="DocumentTemplateCode">The template that satisfies it, where one does.</param>
public sealed record RequireAction(
    string Code,
    string Name,
    string? DocumentTemplateCode = null) : RuleAction;

/// <summary>
/// Firing is itself the finding.
/// </summary>
/// <remarks>
/// For prohibitions rather than requirements — a fee above its cap, a conditional
/// incentive in an advertised price. There is nothing to satisfy: the deal is
/// structured in a way the rule forbids, and the fix is to restructure it.
/// </remarks>
/// <param name="Message">What is wrong, in the words a dealer should read.</param>
public sealed record ProhibitAction(string Message) : RuleAction;

/// <summary>The deal is on a clock.</summary>
/// <param name="Code">A stable deadline code, e.g. <c>PRE_REGISTRATION</c>.</param>
/// <param name="Days">How many days from the trigger date.</param>
/// <param name="BusinessDays">Whether those are business days or calendar days.</param>
/// <remarks>
/// The number lives here, in a versioned rule with a citation — never as a
/// constant in the titling code. A statutory deadline that changes must be a new
/// rule version, and deals written before it must keep the deadline they were
/// given.
/// </remarks>
public sealed record DeadlineAction(
    string Code,
    int Days,
    bool BusinessDays = true) : RuleAction;

/// <summary>
/// One version of one rule: the thing that is actually evaluated.
/// </summary>
/// <remarks>
/// <para>
/// The typed form of a <c>compliance.regulatory_rule_version</c> row. Append-only
/// in the database and immutable here, because a rule that can be edited is a rule
/// that can silently restate what the engine told a dealer last year.
/// </para>
/// <para>
/// <see cref="SourceVersionId"/> is not optional. A rule with no cited source is
/// somebody's recollection, and this system does not evaluate deals against
/// recollections.
/// </para>
/// </remarks>
public sealed record RegulatoryRuleVersion
{
    /// <summary>The rule version row.</summary>
    public required Guid Id { get; init; }

    /// <summary>The rule's stable code, e.g. <c>OK-TEMP-TAG-001</c>.</summary>
    public required string RuleCode { get; init; }

    /// <summary>The semantic version of this rule.</summary>
    public required string Version { get; init; }

    /// <summary>The jurisdiction whose law this is, e.g. <c>US-OK</c>.</summary>
    public required string JurisdictionCode { get; init; }

    /// <summary>Which lifecycle event this rule is evaluated on.</summary>
    public required string TriggerEvent { get; init; }

    /// <summary>How seriously a firing is taken.</summary>
    public required RuleSeverity Severity { get; init; }

    /// <summary>Where in the lifecycle this version sits.</summary>
    public required RuleStatus Status { get; init; }

    /// <summary>First date this version governs.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Last date this version governs, exclusive. Null is open-ended.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>Whether a named human approved it.</summary>
    public required bool IsApproved { get; init; }

    /// <summary>The archived source text this was written against.</summary>
    public required Guid SourceVersionId { get; init; }

    /// <summary>When the conditions hold, the rule fires.</summary>
    public required ConditionNode Conditions { get; init; }

    /// <summary>What firing produces.</summary>
    public IReadOnlyList<RuleAction> Actions { get; init; } = [];

    /// <summary>What a human reads when this fires.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Whether this version governs a deal written on <paramref name="date"/>.
    /// </summary>
    /// <remarks>
    /// Approval is checked here rather than left to the caller, because "did we
    /// remember to filter out the drafts" is exactly the sort of thing that is
    /// remembered in one query and forgotten in the next.
    /// </remarks>
    public bool GovernsOn(DateOnly date) =>
        IsApproved
        && Status is RuleStatus.Active or RuleStatus.Scheduled or RuleStatus.Approved
        && date >= EffectiveFrom
        && (EffectiveTo is null || date < EffectiveTo.Value);
}
