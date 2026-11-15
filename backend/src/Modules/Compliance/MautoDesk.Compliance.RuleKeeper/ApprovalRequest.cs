using System.Globalization;

namespace MautoDesk.Compliance.RuleKeeper;

/// <summary>
/// What an operator is asking for: one draft, approved by one named person, for
/// one stretch of time.
/// </summary>
/// <remarks>
/// <para>
/// The reviewer's name and the sign-off note are not conveniences. An approval
/// that cannot say who made it is the thing `rule_pack_approval.sql` already
/// rejects, and an approval with no reasoning attached is unreviewable a year
/// later when somebody asks why the system believed what it believed.
/// </para>
/// <para>
/// The note arrives as a <em>file</em> rather than a command-line string on
/// purpose. A sign-off is a document: it gets written, read, and kept. Typing it
/// between quotes in a shell encourages one line, and one line is not a legal
/// review.
/// </para>
/// </remarks>
public sealed record ApprovalRequest
{
    /// <summary>The draft version being approved.</summary>
    public required Guid DraftVersionId { get; init; }

    /// <summary>The semantic version the approved row will carry, e.g. <c>1.0.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The reviewer's real name, recorded in <c>approved_by</c>.</summary>
    public required string ReviewedBy { get; init; }

    /// <summary>Who ran the tool, recorded on the audit event.</summary>
    public required string OperatorName { get; init; }

    /// <summary>The first date the approved reading governs.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>The day the approved reading stops governing, exclusive. Null means open-ended.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>The sign-off, read from a file.</summary>
    public required string Note { get; init; }

    /// <summary>Where that file was, for the audit record.</summary>
    public required string NotePath { get; init; }

    /// <summary>The repository root, when the archived source should be re-hashed.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Whether to write. False runs every check and changes nothing.</summary>
    public bool Commit { get; init; }

    /// <summary>Parses the command line, or explains what is missing.</summary>
    /// <remarks>
    /// Hand-rolled rather than a parser package: six options, one verb, and a
    /// dependency that reaches the credential-handling path should earn its
    /// place.
    /// </remarks>
    public static (ApprovalRequest? Request, string? Error) Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var commit = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--commit")
            {
                commit = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                return (null, $"Unexpected argument '{arg}'.");
            }

            if (i + 1 >= args.Length)
            {
                return (null, $"{arg} needs a value.");
            }

            values[arg] = args[++i];
        }

        foreach (var required in new[] { "--draft", "--version", "--reviewed-by", "--operator", "--effective-from", "--note-file" })
        {
            if (!values.ContainsKey(required))
            {
                return (null, $"{required} is required.");
            }
        }

        if (!Guid.TryParse(values["--draft"], out var draftId))
        {
            return (null, "--draft must be the uuid of an existing draft rule version.");
        }

        if (!DateOnly.TryParse(values["--effective-from"], CultureInfo.InvariantCulture, out var from))
        {
            return (null, "--effective-from must be a date, e.g. 2026-11-01.");
        }

        DateOnly? to = null;

        if (values.TryGetValue("--effective-to", out var toText))
        {
            if (!DateOnly.TryParse(toText, CultureInfo.InvariantCulture, out var parsedTo))
            {
                return (null, "--effective-to must be a date, e.g. 2027-01-01.");
            }

            to = parsedTo;
        }

        var notePath = values["--note-file"];

        if (!File.Exists(notePath))
        {
            return (null, $"The sign-off note '{notePath}' does not exist.");
        }

        var note = File.ReadAllText(notePath).Trim();

        if (note.Length < 40)
        {
            return (null,
                $"The sign-off note in '{notePath}' is {note.Length} characters. A reviewer's " +
                "approval needs to say what was reviewed and on what basis; this is too short " +
                "to be that.");
        }

        return (new ApprovalRequest
        {
            DraftVersionId = draftId,
            Version = values["--version"].Trim(),
            ReviewedBy = values["--reviewed-by"].Trim(),
            OperatorName = values["--operator"].Trim(),
            EffectiveFrom = from,
            EffectiveTo = to,
            Note = note,
            NotePath = Path.GetFullPath(notePath),
            RepositoryRoot = values.TryGetValue("--repo-root", out var root) ? root : null,
            Commit = commit,
        }, null);
    }
}
