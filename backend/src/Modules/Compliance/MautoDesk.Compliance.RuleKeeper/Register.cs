using Npgsql;

namespace MautoDesk.Compliance.RuleKeeper;

/// <summary>One thing waiting on a human.</summary>
/// <param name="Id">The register row.</param>
/// <param name="Kind">What sort of item it is.</param>
/// <param name="Jurisdiction">Whose law.</param>
/// <param name="Discipline">Which profession it needs.</param>
/// <param name="Question">What the reviewer is being asked.</param>
/// <param name="Reading">Where the drafter got to, and why they stopped.</param>
/// <param name="Reference">The document to read first.</param>
/// <param name="BlocksGoLive">Whether go-live waits on it.</param>
public sealed record RegisterItem(
    Guid Id,
    string Kind,
    string Jurisdiction,
    string Discipline,
    string Question,
    string? Reading,
    string? Reference,
    bool BlocksGoLive);

/// <summary>
/// Reads <c>compliance.pending_approval</c>: what is waiting, and on whom.
/// </summary>
/// <remarks>
/// <para>
/// The register exists so that engine work does not wait on procurement. Before
/// it, the list of what a reviewer had to dispose of lived in prose across three
/// documents, which is to say it lived nowhere a tool could read. When counsel is
/// retained, the work is to walk this table.
/// </para>
/// <para>
/// Read-only here by design. Nothing in this register activates a rule: a row
/// records that a draft is waiting, and only an approved version row makes a
/// draft usable. The two approval tools close register items as a side effect of
/// the approvals they write, inside the same transaction.
/// </para>
/// </remarks>
public static class Register
{
    /// <summary>Loads the open items, in the order a reviewer should work them.</summary>
    /// <param name="connection">A rule-keeper connection.</param>
    /// <param name="discipline">Limit to one profession, or null for all.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<List<RegisterItem>> LoadOpenAsync(
        NpgsqlConnection connection,
        string? discipline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        const string Sql = """
            select id, item_kind, jurisdiction_code, reviewer_discipline, question,
                   drafter_reading, reference_document, blocks_go_live
              from compliance.pending_approval
             where resolved_at is null
               and (@discipline is null or reviewer_discipline = @discipline)
             order by sort_order, question
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue(
            "discipline", (object?)discipline ?? DBNull.Value);

        var items = new List<RegisterItem>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new RegisterItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(6),
                reader.GetBoolean(7)));
        }

        return items;
    }

    /// <summary>Prints the register the way somebody would want to read it.</summary>
    /// <param name="items">The open items.</param>
    /// <param name="writer">Where to write.</param>
    public static void Print(IReadOnlyList<RegisterItem> items, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(writer);

        if (items.Count == 0)
        {
            writer.WriteLine("Nothing is waiting on a reviewer.");
            writer.WriteLine();
            writer.WriteLine(
                "That is either very good news or a sign the register was never seeded — "
                + "V0017 seeds it, and it asserts its own counts.");
            return;
        }

        foreach (var group in items.GroupBy(item => item.Discipline).OrderBy(group => group.Key))
        {
            writer.WriteLine();
            writer.WriteLine($"=== {Describe(group.Key)} — {group.Count()} item(s)");

            foreach (var item in group)
            {
                writer.WriteLine();
                writer.WriteLine($"  {item.Id}");
                writer.WriteLine(
                    $"  {item.Jurisdiction}  {item.Kind}"
                    + (item.BlocksGoLive ? "  [blocks go-live]" : string.Empty));
                writer.WriteLine();
                Wrap(item.Question, "    ", writer);

                if (item.Reading is { Length: > 0 } reading)
                {
                    writer.WriteLine();
                    writer.WriteLine("    Drafter:");
                    Wrap(reading, "      ", writer);
                }

                if (item.Reference is { Length: > 0 } reference)
                {
                    writer.WriteLine();
                    writer.WriteLine($"    Read first: {reference}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine(
            $"{items.Count} item(s) open, {items.Count(item => item.BlocksGoLive)} blocking "
            + "go-live.");
        writer.WriteLine(
            "None of this is approved and none of it is active. A rule becomes active only when "
            + "rulekeeper or taxkeeper writes an approved version row carrying a reviewer's name.");
    }

    private static string Describe(string discipline) => discipline switch
    {
        "attorney" => "For an Oklahoma-licensed attorney",
        "cpa_or_tax_counsel" => "For a CPA or tax counsel",
        "project_owner" => "For the project owner",
        _ => "For either reviewer",
    };

    /// <summary>Wraps prose at a readable width, because a terminal is not a database client.</summary>
    private static void Wrap(string text, string indent, TextWriter writer)
    {
        const int Width = 76;

        var line = new System.Text.StringBuilder(indent);

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > indent.Length && line.Length + word.Length + 1 > Width)
            {
                writer.WriteLine(line.ToString());
                line.Clear().Append(indent);
            }

            if (line.Length > indent.Length)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > indent.Length)
        {
            writer.WriteLine(line.ToString());
        }
    }
}
