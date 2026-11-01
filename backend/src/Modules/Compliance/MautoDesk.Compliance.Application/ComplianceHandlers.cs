using MautoDesk.Compliance.Contracts;
using MautoDesk.Compliance.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Compliance.Application;

/// <summary>A rule version row, before its definition has been parsed.</summary>
/// <param name="Id">The rule version row.</param>
/// <param name="RuleCode">The rule's stable code.</param>
/// <param name="Version">The semantic version.</param>
/// <param name="JurisdictionCode">Whose law this is.</param>
/// <param name="TriggerEvent">Which lifecycle event it is evaluated on.</param>
/// <param name="Severity">blocking, warning, review or informational.</param>
/// <param name="Status">draft, review, approved, scheduled, active or retired.</param>
/// <param name="EffectiveFrom">First date it governs.</param>
/// <param name="EffectiveTo">Last date it governs, exclusive.</param>
/// <param name="IsApproved">Whether a named human approved it.</param>
/// <param name="SourceVersionId">The archived source text behind it.</param>
/// <param name="DefinitionJson">The <c>definition</c> jsonb, verbatim.</param>
/// <param name="Message">What a human reads when it fires.</param>
/// <param name="SourceCitation">The statute or publication reference.</param>
public sealed record RegulatoryRuleRow(
    Guid Id,
    string RuleCode,
    string Version,
    string JurisdictionCode,
    string TriggerEvent,
    string Severity,
    string Status,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsApproved,
    Guid SourceVersionId,
    string DefinitionJson,
    string Message,
    string? SourceCitation);

/// <summary>Reads regulatory rules.</summary>
public interface IRegulatoryRuleStore
{
    /// <summary>
    /// Every rule that could govern a jurisdiction on a date for a trigger.
    /// </summary>
    /// <remarks>
    /// Returns approved rules only. Unlike the tax rule set — where an unapproved
    /// row is worth surfacing so a human knows rules exist but are unreviewed — an
    /// unapproved <em>regulatory</em> rule has nothing useful to say about a
    /// specific deal, and the evaluator would discard it anyway.
    /// </remarks>
    public Task<IReadOnlyList<RegulatoryRuleRow>> FindApplicableAsync(
        string jurisdictionCode,
        string triggerEvent,
        DateOnly onDate,
        CancellationToken cancellationToken);

    /// <summary>The pack version in force, for the run record.</summary>
    /// <summary>
    /// Whether this jurisdiction is one the system knows as a law-making body.
    /// </summary>
    /// <param name="jurisdictionCode">The code to look for, e.g. <c>US-OK</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<bool> JurisdictionExistsAsync(
        string jurisdictionCode,
        CancellationToken cancellationToken);

    public Task<string> ResolvePackVersionAsync(
        string jurisdictionCode,
        DateOnly onDate,
        CancellationToken cancellationToken);
}

/// <summary>Reads a jurisdiction's holiday calendar.</summary>
public interface IHolidayCalendarStore
{
    /// <summary>
    /// The observed holidays covering a window.
    /// </summary>
    /// <remarks>
    /// Returns whatever is loaded, including nothing.
    /// <see cref="BusinessDayCalendar.ForJurisdiction"/> is what refuses an empty
    /// calendar — this store's job is to report the truth, not to decide what to do
    /// about it.
    /// </remarks>
    public Task<IReadOnlyList<DateOnly>> GetHolidaysAsync(
        string jurisdictionCode,
        CancellationToken cancellationToken);
}

/// <summary>Writes compliance runs and their results.</summary>
public interface IComplianceRunStore
{
    /// <summary>
    /// Persists a run and its verdicts, in one transaction.
    /// </summary>
    /// <remarks>
    /// The id is supplied by the caller rather than generated here, so the caller
    /// can record an audit entry naming the run before it is written. The
    /// implementation flushes the caller's tracked changes inside the same
    /// transaction, which is what makes "the run committed and its audit entry did
    /// not" impossible.
    /// </remarks>
    public Task SaveAsync(
        Guid runId,
        Guid tenantId,
        Guid dealId,
        ComplianceRun run,
        string inputJson,
        Guid? userId,
        CancellationToken cancellationToken);

    /// <summary>One specific run, by id.</summary>
    /// <remarks>
    /// The caller that has just written a run reads it back through this rather
    /// than through <see cref="GetLatestAsync"/>. "The latest run for this deal"
    /// is not the same claim as "the run I just created" — two people evaluating
    /// the same deal at once would each be handed whichever landed second.
    /// </remarks>
    public Task<ComplianceRunDto?> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The most recent run for a deal, or null.</summary>
    public Task<ComplianceRunDto?> GetLatestAsync(Guid dealId, CancellationToken cancellationToken);
}

/// <summary>Supplies the facts about a deal that rules are evaluated against.</summary>
/// <remarks>
/// <para>
/// Deliberately a port rather than a concrete builder. The facts come from
/// several modules — the deal and its calculation from Sales, documents from
/// Documents, the title from Titling — and Compliance must not take a reference
/// on any of them. The host composes the implementation from each module's
/// public Contracts surface (ADR-0001).
/// </para>
/// <para>
/// <b>Nothing here may come from the client.</b> Every fact is read from the
/// database on the server. A rule that can be satisfied by a field in a request
/// body is not a compliance control, it is a suggestion.
/// </para>
/// </remarks>
public interface IRegulatoryContextBuilder
{
    /// <summary>Assembles the facts, or explains why it cannot.</summary>
    public Task<Result<DealFacts>> BuildAsync(Guid dealId, CancellationToken cancellationToken);
}

/// <summary>The facts about one deal, plus the context they were assembled into.</summary>
/// <param name="Context">The flattened facts and satisfied requirements.</param>
/// <param name="JurisdictionCode">Whose law governs, e.g. <c>US-OK</c>.</param>
/// <param name="DealDate">The date that governs rule resolution.</param>
public sealed record DealFacts(
    RegulatoryContext Context,
    string JurisdictionCode,
    DateOnly DealDate);

/// <summary>
/// Evaluates a deal and records the run.
/// </summary>
/// <remarks>
/// Orchestration only. It loads rules, builds facts, calls the pure evaluator and
/// persists exactly what came back. No rule is interpreted here, and no verdict is
/// adjusted — the moment this class starts deciding anything, the run stops being
/// a faithful record of what the approved rules said.
/// </remarks>
public sealed class ComplianceEvaluationHandler
{
    private readonly IRegulatoryRuleStore _rules;
    private readonly IRegulatoryContextBuilder _facts;
    private readonly IHolidayCalendarStore _holidays;
    private readonly IComplianceRunStore _runs;
    private readonly IAuditLog _audit;
    private readonly ITenantContext _tenant;

    /// <summary>Creates the handler.</summary>
    public ComplianceEvaluationHandler(
        IRegulatoryRuleStore rules,
        IRegulatoryContextBuilder facts,
        IHolidayCalendarStore holidays,
        IComplianceRunStore runs,
        IAuditLog audit,
        ITenantContext tenant)
    {
        _rules = rules;
        _facts = facts;
        _holidays = holidays;
        _runs = runs;
        _audit = audit;
        _tenant = tenant;
    }

    /// <summary>Evaluates a deal against the rules in force on its date.</summary>
    /// <param name="dealId">The deal.</param>
    /// <param name="triggerEvent">Which lifecycle event to evaluate, e.g. <c>DEAL_SOLD</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<ComplianceRunDto>> EvaluateAsync(
        Guid dealId,
        string triggerEvent,
        CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(CompliancePermissions.EvaluationRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to evaluate compliance.");
        }

        var facts = await _facts.BuildAsync(dealId, cancellationToken).ConfigureAwait(false);

        if (facts.IsFailure)
        {
            return facts.Error!;
        }

        var context = facts.Value!;

        // A deal in a state we hold no law for is a refusal, not an error. Without
        // this the first thing to notice is the foreign key on compliance_run, which
        // surfaces as a 500 — so every dealer in a state with no rule pack would see
        // the evaluation fail as though the system were broken, rather than be told
        // that their jurisdiction is not covered yet.
        var known = await _rules
            .JurisdictionExistsAsync(context.JurisdictionCode, cancellationToken)
            .ConfigureAwait(false);

        if (!known)
        {
            return Error.Validation(
                "compliance.jurisdiction_not_covered",
                $"{context.JurisdictionCode} is not a jurisdiction this system holds law for, so " +
                "there is nothing to evaluate this deal against. A rule pack has to be loaded " +
                "and approved before compliance can run here.");
        }

        var rows = await _rules
            .FindApplicableAsync(context.JurisdictionCode, triggerEvent, context.DealDate, cancellationToken)
            .ConfigureAwait(false);

        var rules = new List<RegulatoryRuleVersion>();

        foreach (var row in rows)
        {
            var parsed = ToRuleVersion(row);

            // One malformed rule fails the whole evaluation rather than being
            // skipped. A compliance run that quietly omitted a blocking rule would
            // report GREEN on a deal that should be RED — and nobody would know.
            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            rules.Add(parsed.Value!);
        }

        var calendar = await BuildCalendarAsync(context.JurisdictionCode, rules, cancellationToken)
            .ConfigureAwait(false);

        if (calendar.IsFailure)
        {
            return calendar.Error!;
        }

        var packVersion = await _rules
            .ResolvePackVersionAsync(context.JurisdictionCode, context.DealDate, cancellationToken)
            .ConfigureAwait(false);

        var run = RuleEvaluator.Evaluate(new EvaluationRequest
        {
            TriggerEvent = triggerEvent,
            JurisdictionCode = context.JurisdictionCode,
            EvaluatedOn = context.DealDate,
            TriggerDate = context.DealDate,
            RulePackVersion = packVersion,
            Context = context.Context,
            Rules = rules,
            Calendar = calendar.Value,
        });

        var runId = Guid.CreateVersion7();

        // Recorded BEFORE the store writes, and deliberately so. IAuditLog.Record
        // only adds to the change tracker; the entry lands when somebody saves.
        // This handler owns no unit of work, so an entry recorded afterwards was
        // simply never persisted — every compliance evaluation went unaudited,
        // silently, which is precisely what the ledger exists to prevent.
        // SaveAsync now flushes the tracker inside its own transaction, so the
        // entry commits with the run or not at all.
        _audit.Record(new AuditEntry
        {
            Action = "compliance.evaluated",
            EntitySchema = "compliance",
            EntityType = "compliance_run",
            EntityId = runId,
            After = new
            {
                Status = run.OverallStatus.ToString(),
                run.BlockingCount,
                run.WarningCount,
                run.EngineVersion,
                run.RulePackVersion,
            },
        });

        await _runs
            .SaveAsync(
                runId, _tenant.RequireTenantId(), dealId, run, context.Context.Canonical(),
                _tenant.UserId, cancellationToken)
            .ConfigureAwait(false);

        // By id, not "the latest for this deal". Reading back the newest row would
        // hand a caller somebody else's evaluation the moment two people work the
        // same deal at once — and, less dramatically, is ambiguous whenever two
        // runs share a timestamp.
        var dto = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);

        return dto is null
            ? Error.NotFound("compliance.run_not_found", "The run was written but could not be read back.")
            : dto;
    }

    /// <summary>Reads the most recent run for a deal.</summary>
    public async Task<Result<ComplianceRunDto>> GetLatestAsync(
        Guid dealId,
        CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(CompliancePermissions.EvaluationRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view compliance results.");
        }

        var dto = await _runs.GetLatestAsync(dealId, cancellationToken).ConfigureAwait(false);

        return dto is null
            ? Error.NotFound("compliance.run_not_found", "This deal has not been evaluated yet.")
            : dto;
    }

    /// <summary>
    /// Loads a calendar only when a rule actually needs one.
    /// </summary>
    /// <remarks>
    /// A jurisdiction with no deadline-bearing rules does not need a holiday
    /// calendar, and demanding one would block evaluation for no reason. When a
    /// rule <em>does</em> carry a deadline and the calendar is empty, that is a
    /// refusal with an explanation rather than a confident wrong date.
    /// </remarks>
    private async Task<Result<BusinessDayCalendar?>> BuildCalendarAsync(
        string jurisdictionCode,
        IReadOnlyList<RegulatoryRuleVersion> rules,
        CancellationToken cancellationToken)
    {
        var needsCalendar = rules.Any(rule => rule.Actions.OfType<DeadlineAction>().Any());

        if (!needsCalendar)
        {
            return (BusinessDayCalendar?)null;
        }

        var holidays = await _holidays
            .GetHolidaysAsync(jurisdictionCode, cancellationToken)
            .ConfigureAwait(false);

        if (holidays.Count == 0)
        {
            return Error.Validation(
                "compliance.no_holiday_calendar",
                $"A rule for {jurisdictionCode} sets a business-day deadline, but no holiday " +
                "calendar has been loaded for that jurisdiction. A deadline computed without one " +
                "is wrong on every holiday. Populate compliance.jurisdiction_holiday from a cited " +
                "source before evaluating.");
        }

        return BusinessDayCalendar.ForJurisdiction(jurisdictionCode, holidays);
    }

    private static Result<RegulatoryRuleVersion> ToRuleVersion(RegulatoryRuleRow row)
    {
        var definition = RuleDefinitionSerializer.Parse(row.RuleCode, row.DefinitionJson);

        if (definition.IsFailure)
        {
            return definition.Error!;
        }

        if (!TryParse<RuleSeverity>(row.Severity, out var severity))
        {
            return Error.Validation(
                "rule.invalid_severity",
                $"{row.RuleCode} has severity '{row.Severity}', which is not one the engine knows.");
        }

        if (!TryParse<RuleStatus>(row.Status, out var status))
        {
            return Error.Validation(
                "rule.invalid_status",
                $"{row.RuleCode} has status '{row.Status}', which is not one the engine knows.");
        }

        return new RegulatoryRuleVersion
        {
            Id = row.Id,
            RuleCode = row.RuleCode,
            Version = row.Version,
            JurisdictionCode = row.JurisdictionCode,
            TriggerEvent = row.TriggerEvent,
            Severity = severity,
            Status = status,
            EffectiveFrom = row.EffectiveFrom,
            EffectiveTo = row.EffectiveTo,
            IsApproved = row.IsApproved,
            SourceVersionId = row.SourceVersionId,
            Conditions = definition.Value!.Conditions,
            Actions = definition.Value.Actions,
            Message = row.Message,
        };
    }

    private static bool TryParse<T>(string value, out T parsed)
        where T : struct, Enum =>
        Enum.TryParse(
            value.Replace("_", string.Empty, StringComparison.Ordinal),
            ignoreCase: true,
            out parsed) && Enum.IsDefined(parsed);
}
