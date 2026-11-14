using MautoDesk.Compliance.RuleKeeper;

// -----------------------------------------------------------------------------
// rulekeeper — approves a regulatory rule version.
//
// Validation-only by default. It prints every check and exits without writing
// unless --commit is passed, because the default behaviour of a tool that can
// write the law should be to tell you what it would do.
//
// The connection string comes from RULEKEEPER_CONNECTION rather than an
// argument, so a credential never lands in shell history or a process list.
// -----------------------------------------------------------------------------

const string Usage = """
    rulekeeper — approve a regulatory rule version

      rulekeeper --draft <uuid> --version <semver> --reviewed-by "<name>"
                 --operator "<name>" --effective-from <yyyy-mm-dd>
                 [--effective-to <yyyy-mm-dd>] --note-file <path>
                 [--repo-root <path>] [--commit]

    Options
      --draft           The draft rule version to approve.
      --version         The version the approved row will carry, e.g. 1.0.0.
                        It must differ from the draft's: an approval is a new row.
      --reviewed-by     The reviewer's real name, recorded in approved_by.
      --operator        Whoever is running this, recorded on the audit event.
      --effective-from  First date the approved reading governs.
      --effective-to    First date it no longer governs. Omit for open-ended.
      --note-file       The reviewer's written sign-off. Required, and kept.
      --repo-root       If given, the archived source is re-hashed from disk.
      --commit          Actually write. Without it nothing is changed.

    Environment
      RULEKEEPER_CONNECTION   A connection string for mautodesk_rulekeeper.

    Exit codes
      0  every check passed (and the approval was written, with --commit)
      1  a check failed, or the write was refused
      2  the command line or the environment is wrong
    """;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Usage);
    return 0;
}

var (request, parseError) = ApprovalRequest.Parse(args);

if (parseError is not null || request is null)
{
    Console.Error.WriteLine($"rulekeeper: {parseError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("RULEKEEPER_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "rulekeeper: RULEKEEPER_CONNECTION is not set. It must be a connection string for "
        + "mautodesk_rulekeeper — not mautodesk_app, which cannot write the law, and not a "
        + "superuser, which can write everything.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var store = new ApprovalStore(connectionString);

try
{
    await using var connection = await store.OpenAsync(cancellation.Token);

    var draft = await ApprovalStore.LoadDraftAsync(connection, request.DraftVersionId, cancellation.Token);

    if (draft is null)
    {
        Console.Error.WriteLine(
            $"rulekeeper: no rule version with id {request.DraftVersionId}. The draft ids are in "
            + "the migration that seeded the pack.");
        return 1;
    }

    var tests = await ApprovalStore.LoadTestsAsync(connection, draft.Id, cancellation.Token);
    var approved = await ApprovalStore.LoadApprovedAsync(connection, draft.RuleId, cancellation.Token);
    var holidayYears = await ApprovalStore.LoadHolidayYearsAsync(
        connection, draft.JurisdictionCode, cancellation.Token);

    Console.WriteLine($"Rule         {draft.RuleCode}  ({draft.JurisdictionCode}, {draft.Severity})");
    Console.WriteLine($"Draft        {draft.Version}  {draft.Id}");
    Console.WriteLine($"Source       {draft.SourceCitation}");
    Console.WriteLine($"Reviewer     {request.ReviewedBy}");
    Console.WriteLine($"Approving as {request.Version}");
    Console.WriteLine();

    var checks = ApprovalChecks.Run(request, draft, tests, approved, holidayYears);

    foreach (var check in checks)
    {
        Console.WriteLine($"  [{(check.Passed ? "ok" : "NO")}] {check.Name}");
        Console.WriteLine($"         {check.Detail}");
    }

    Console.WriteLine();

    var failed = checks.Where(check => !check.Passed).ToList();

    if (failed.Count > 0)
    {
        Console.Error.WriteLine(
            $"rulekeeper: {failed.Count} of {checks.Count} checks failed. Nothing was written.");
        Console.Error.WriteLine(
            "None of these are legal judgements — they are the mechanical preconditions. A "
            + "reviewer's sign-off cannot waive them, because what they protect is the ability "
            + "to show later what was approved and against what.");
        return 1;
    }

    if (!request.Commit)
    {
        Console.WriteLine(
            $"All {checks.Count} checks passed. Nothing was written: pass --commit to approve.");
        Console.WriteLine();
        Console.WriteLine(
            "Once written, the row is permanent. compliance.regulatory_rule_version is "
            + "append-only, so an approval cannot be edited or withdrawn — a correction is a "
            + "further version with its own effective dates.");
        return 0;
    }

    var approvedId = await ApprovalStore.ApproveAsync(connection, draft, request, cancellation.Token);

    Console.WriteLine($"Approved. {draft.RuleCode} {request.Version} is now active from "
        + $"{request.EffectiveFrom:yyyy-MM-dd}"
        + (request.EffectiveTo is { } to ? $" to {to:yyyy-MM-dd}." : ", open-ended."));
    Console.WriteLine($"Rule version id  {approvedId}");
    Console.WriteLine($"Audit event      compliance.rule_version.approved, platform scope");
    Console.WriteLine();
    Console.WriteLine(
        "The engine will evaluate this rule from now on. A deal in this jurisdiction no longer "
        + "reports GREEN because nothing is approved.");

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("rulekeeper: cancelled. Nothing was written.");
    return 1;
}
catch (Npgsql.PostgresException exception)
{
    // The database has guards this tool deliberately does not duplicate — the
    // overlap exclusion constraint most of all. Reaching one is a refusal, not a
    // crash, and the operator needs to know which.
    Console.Error.WriteLine($"rulekeeper: the database refused the write — {exception.MessageText}");

    if (exception.ConstraintName is { } constraint)
    {
        Console.Error.WriteLine($"  constraint: {constraint}");

        if (constraint.Contains("no_overlap", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "  Two approved versions of one rule may not cover the same date. Close the "
                + "existing version's effective_to first, by approving a replacement that starts "
                + "where it ends.");
        }
    }

    return 1;
}
