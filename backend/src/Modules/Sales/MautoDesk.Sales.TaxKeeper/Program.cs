using System.Globalization;
using MautoDesk.Sales.TaxKeeper;

// -----------------------------------------------------------------------------
// taxkeeper — approves a tax rule set.
//
// The sibling of rulekeeper, and deliberately a separate executable: validating
// a tax rule set means deserializing it with the Sales module's own serializer
// and asking TaxRuleSet.BandError whether its bands cohere, so the tool belongs
// in the Sales module. Reimplementing those checks elsewhere would give the
// approval path a second, drifting definition of a valid rule set.
//
// Validation-only by default, and the connection string comes from the
// environment, for the same two reasons as its sibling.
// -----------------------------------------------------------------------------

const string Usage = """
    taxkeeper — approve a tax rule set

      taxkeeper --draft <uuid> --version <int> --reviewed-by "<name>"
                --operator "<name>" --effective-from <yyyy-mm-dd>
                [--effective-to <yyyy-mm-dd>] --note-file <path> [--commit]

    Options
      --draft           The draft sales.rule_set row to approve.
      --version         The version number the approved row will carry. It must
                        differ from the draft's: an approval is a new row.
      --reviewed-by     The CPA or tax counsel's real name, recorded in
                        approved_by. This is the whole deliverable.
      --operator        Whoever is running this, recorded on the audit event.
      --effective-from  First date the approved figures govern.
      --effective-to    First date they no longer govern. Omit for open-ended.
      --note-file       The reviewer's written sign-off. Required, and kept.
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

var (request, parseError) = Parse(args);

if (parseError is not null || request is null)
{
    Console.Error.WriteLine($"taxkeeper: {parseError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("RULEKEEPER_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "taxkeeper: RULEKEEPER_CONNECTION is not set. It must be a connection string for "
        + "mautodesk_rulekeeper — not mautodesk_app, which holds SELECT and nothing else on "
        + "sales.rule_set so that a compromised API cannot rewrite the tax law.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var store = new TaxApprovalStore(connectionString);

try
{
    await using var connection = await store.OpenAsync(cancellation.Token);

    var draft = await TaxApprovalStore.LoadDraftAsync(connection, request.DraftId, cancellation.Token);

    if (draft is null)
    {
        Console.Error.WriteLine(
            $"taxkeeper: no rule set with id {request.DraftId}. The drafted Oklahoma row is "
            + "seeded by V0016, and compliance.pending_approval lists what is waiting.");
        return 1;
    }

    var approved = await TaxApprovalStore.LoadApprovedAsync(
        connection, draft.JurisdictionId, cancellation.Token);

    Console.WriteLine($"Jurisdiction  {draft.JurisdictionName}");
    Console.WriteLine($"Draft         version {draft.Version}, rules schema {draft.SchemaVersion}");
    Console.WriteLine($"Source        {draft.SourceCitation}");
    Console.WriteLine($"Reviewer      {request.ReviewedBy}");
    Console.WriteLine($"Approving as  version {request.Version}");
    Console.WriteLine();

    var checks = TaxApprovalChecks.Run(request, draft, approved);

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
            $"taxkeeper: {failed.Count} of {checks.Count} checks failed. Nothing was written.");
        Console.Error.WriteLine(
            "None of these are tax judgements — they are the mechanical preconditions. A "
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
            "Once written, the row is permanent. sales.rule_set is append-only as of V0017, so "
            + "an approval cannot be edited or withdrawn — a correction is a further version "
            + "with its own effective dates, and the figures on contracts already written stay "
            + "traceable to the version that produced them.");
        return 0;
    }

    var approvedId = await TaxApprovalStore.ApproveAsync(
        connection, draft, request, cancellation.Token);

    Console.WriteLine(
        $"Approved. {draft.JurisdictionName} tax rule set version {request.Version} governs from "
        + $"{request.EffectiveFrom:yyyy-MM-dd}"
        + (request.EffectiveTo is { } to ? $" to {to:yyyy-MM-dd}." : ", open-ended."));
    Console.WriteLine($"Rule set id   {approvedId}");
    Console.WriteLine($"Audit event   sales.rule_set.approved, platform scope");
    Console.WriteLine($"Register      any item waiting on this draft is now resolved");
    Console.WriteLine();
    Console.WriteLine(
        "The deal engine will price deals in this jurisdiction from now on. It no longer "
        + "refuses with deal.rule_set_unapproved.");

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("taxkeeper: cancelled. Nothing was written.");
    return 1;
}
catch (Npgsql.PostgresException exception)
{
    // The database holds guards this tool deliberately does not duplicate, the
    // overlap exclusion constraint most of all. Reaching one is a refusal rather
    // than a crash, and the operator needs to know which.
    Console.Error.WriteLine($"taxkeeper: the database refused the write — {exception.MessageText}");

    if (exception.ConstraintName is { } constraint)
    {
        Console.Error.WriteLine($"  constraint: {constraint}");

        if (constraint.Contains("no_overlap", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "  Two approved rule sets may not cover the same date for one jurisdiction, "
                + "because that is two possible prices for one deal. Approve a replacement that "
                + "starts where the existing one ends.");
        }

        if (constraint.Contains("version_uq", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "  That version number is taken for this jurisdiction. Pick the next one.");
        }
    }

    return 1;
}

// -----------------------------------------------------------------------------
// Argument parsing, hand-rolled for the same reason as rulekeeper's: a tool that
// writes the tax law should not pull in a command-line framework and its
// transitive dependencies.
// -----------------------------------------------------------------------------
static (TaxApprovalRequest? Request, string? Error) Parse(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var commit = false;

    for (var index = 0; index < args.Length; index++)
    {
        var argument = args[index];

        if (argument == "--commit")
        {
            commit = true;
            continue;
        }

        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return (null, $"unexpected argument '{argument}'.");
        }

        if (index + 1 >= args.Length)
        {
            return (null, $"{argument} needs a value.");
        }

        values[argument] = args[++index];
    }

    foreach (var required in new[]
             {
                 "--draft", "--version", "--reviewed-by", "--operator",
                 "--effective-from", "--note-file",
             })
    {
        if (!values.ContainsKey(required))
        {
            return (null, $"{required} is required.");
        }
    }

    if (!Guid.TryParse(values["--draft"], out var draftId))
    {
        return (null, $"--draft '{values["--draft"]}' is not a uuid.");
    }

    if (!int.TryParse(values["--version"], CultureInfo.InvariantCulture, out var version)
        || version <= 0)
    {
        return (null,
            $"--version '{values["--version"]}' is not a positive integer. A tax rule set "
            + "version is an int, unlike a rule version's semver.");
    }

    if (!DateOnly.TryParseExact(
            values["--effective-from"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var from))
    {
        return (null, $"--effective-from '{values["--effective-from"]}' is not yyyy-mm-dd.");
    }

    DateOnly? to = null;

    if (values.TryGetValue("--effective-to", out var rawTo))
    {
        if (!DateOnly.TryParseExact(
                rawTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var parsedTo))
        {
            return (null, $"--effective-to '{rawTo}' is not yyyy-mm-dd.");
        }

        to = parsedTo;
    }

    var notePath = values["--note-file"];

    if (!File.Exists(notePath))
    {
        return (null, $"--note-file '{notePath}' does not exist.");
    }

    var note = File.ReadAllText(notePath);

    if (note.Trim().Length < 40)
    {
        // The same floor as rulekeeper's, and for the same reason: a one-line
        // sign-off on a set of tax figures is not a review, and this is the
        // cheapest possible place to say so.
        return (null,
            $"--note-file '{notePath}' holds {note.Trim().Length} characters. A sign-off on tax "
            + "figures that go onto retail contracts needs to say what was reviewed and on what "
            + "basis.");
    }

    return (new TaxApprovalRequest
    {
        DraftId = draftId,
        Version = version,
        ReviewedBy = values["--reviewed-by"],
        OperatorName = values["--operator"],
        EffectiveFrom = from,
        EffectiveTo = to,
        Note = note,
        NotePath = Path.GetFullPath(notePath),
        Commit = commit,
    }, null);
}
