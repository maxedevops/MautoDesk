using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Sales.TaxKeeper;

/// <summary>
/// Reads the draft rule set and writes the approval.
/// </summary>
/// <remarks>
/// Raw SQL and one connection, like its sibling in the Compliance module. This
/// runs as <c>mautodesk_rulekeeper</c>, which holds SELECT on
/// <c>sales.rule_set</c> and INSERT on it and nothing more, so there is nothing
/// for an ORM to map.
/// </remarks>
public sealed class TaxApprovalStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store over a rule-keeper connection string.</summary>
    public TaxApprovalStore(string connectionString) => _connectionString = connectionString;

    /// <summary>Opens a connection, so the caller controls the transaction.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Loads the draft rule set, or null when no such row exists.</summary>
    public static async Task<TaxDraft?> LoadDraftAsync(
        NpgsqlConnection connection,
        Guid ruleSetId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select s.id, s.jurisdiction_id, j.name, s.schema_version, s.version,
                   s.effective_from, s.effective_to, s.approved_at is not null,
                   s.source_citation, s.rules::text, s.notes
              from sales.rule_set s
              join sales.tax_jurisdiction j on j.id = s.jurisdiction_id
             where s.id = @id
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("id", ruleSetId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TaxDraft
        {
            Id = reader.GetGuid(0),
            JurisdictionId = reader.GetGuid(1),
            JurisdictionName = reader.GetString(2),
            SchemaVersion = reader.GetInt32(3),
            Version = reader.GetInt32(4),
            EffectiveFrom = DateOnly.FromDateTime(reader.GetDateTime(5)),
            EffectiveTo = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(6)),
            IsApproved = reader.GetBoolean(7),
            SourceCitation = reader.GetString(8),
            RulesJson = reader.GetString(9),
            Notes = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(10),
        };
    }

    /// <summary>Loads the jurisdiction's already-approved rule sets.</summary>
    public static async Task<List<(int Version, DateOnly From, DateOnly? To)>> LoadApprovedAsync(
        NpgsqlConnection connection,
        Guid jurisdictionId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select version, effective_from, effective_to
              from sales.rule_set
             where jurisdiction_id = @jurisdiction and approved_at is not null
             order by effective_from
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("jurisdiction", jurisdictionId);

        var rows = new List<(int, DateOnly, DateOnly?)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetInt32(0),
                DateOnly.FromDateTime(reader.GetDateTime(1)),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                    ? null
                    : DateOnly.FromDateTime(reader.GetDateTime(2))));
        }

        return rows;
    }

    /// <summary>
    /// Writes the approval, its audit event, and the register closure together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One transaction. An approved tax rule set that nobody can see in the
    /// ledger, or a register item still showing as open after the thing it was
    /// waiting for landed, are both the half-recorded states this path exists to
    /// prevent.
    /// </para>
    /// <para>
    /// The new row copies the draft's <c>rules</c> verbatim with <c>insert ...
    /// select</c>, rather than re-serializing what this tool deserialized. The
    /// figures that get approved are byte-for-byte the figures a reviewer read.
    /// </para>
    /// </remarks>
    public static async Task<Guid> ApproveAsync(
        NpgsqlConnection connection,
        TaxDraft draft,
        TaxApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var approvedId = Guid.NewGuid();

        const string InsertRuleSet = """
            insert into sales.rule_set
              (id, jurisdiction_id, schema_version, version, effective_from, effective_to,
               rules, source_citation, source_url, notes, approved_at, approved_by)
            select @id, s.jurisdiction_id, s.schema_version, @version, @from, @to,
                   s.rules, s.source_citation, s.source_url, @notes, now(), @reviewer
              from sales.rule_set s
             where s.id = @draft_id
            """;

        await using (var command = new NpgsqlCommand(InsertRuleSet, connection, transaction))
        {
            command.Parameters.AddWithValue("id", approvedId);
            command.Parameters.AddWithValue("version", request.Version);
            command.Parameters.AddWithValue("from", request.EffectiveFrom);
            command.Parameters.AddWithValue(
                "to", request.EffectiveTo.HasValue ? request.EffectiveTo.Value : DBNull.Value);
            command.Parameters.AddWithValue("reviewer", request.ReviewedBy);
            command.Parameters.AddWithValue("notes", Notes(draft, request));
            command.Parameters.AddWithValue("draft_id", draft.Id);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string InsertAudit = """
            insert into audit.event
              (tenant_id, actor_type, actor_display, access_reason, action,
               entity_schema, entity_type, entity_id, after_state, metadata)
            values
              (null, 'platform_admin', @operator, @reason, 'sales.rule_set.approved',
               'sales', 'rule_set', @id, @after, @metadata)
            """;

        await using (var command = new NpgsqlCommand(InsertAudit, connection, transaction))
        {
            command.Parameters.AddWithValue("operator", request.OperatorName);
            command.Parameters.AddWithValue(
                "reason",
                $"Approval of the {draft.JurisdictionName} tax rule set on the written sign-off "
                + $"of {request.ReviewedBy}");
            command.Parameters.AddWithValue("id", approvedId);
            command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Jsonb)
            {
                Value = $$"""
                    {
                      "jurisdiction": {{Json(draft.JurisdictionName)}},
                      "version": {{draft.Version}},
                      "approvedVersion": {{request.Version}},
                      "approvedBy": {{Json(request.ReviewedBy)}},
                      "effectiveFrom": {{Json(request.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}},
                      "sourceCitation": {{Json(draft.SourceCitation)}}
                    }
                    """,
            });
            command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb)
            {
                Value = $$"""
                    {
                      "tool": "taxkeeper",
                      "draftRuleSetId": {{Json(draft.Id.ToString())}},
                      "signOffFile": {{Json(request.NotePath)}}
                    }
                    """,
            });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Close the register item, if one was waiting on this draft. Guarded by
        // the trigger from V0017, which permits only the open -> resolved
        // transition and nothing else about the row.
        const string CloseRegister = """
            update compliance.pending_approval
               set resolved_at = now(),
                   resolved_by = @reviewer,
                   resolution = 'approved',
                   resolution_note = @note,
                   approved_rule_set_id = @approved_id
             where tax_rule_set_id = @draft_id and resolved_at is null
            """;

        await using (var command = new NpgsqlCommand(CloseRegister, connection, transaction))
        {
            command.Parameters.AddWithValue("reviewer", request.ReviewedBy);
            command.Parameters.AddWithValue("note", request.Note);
            command.Parameters.AddWithValue("approved_id", approvedId);
            command.Parameters.AddWithValue("draft_id", draft.Id);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return approvedId;
    }

    /// <summary>
    /// The note on the approved row: the reviewer's words above the drafter's.
    /// </summary>
    /// <remarks>
    /// Both are kept, in that order. The drafter's note records what was
    /// UNVERIFIED and why; keeping it under the sign-off means the things the
    /// reviewer was asked about stay readable next to their answer, instead of
    /// being overwritten by it.
    /// </remarks>
    private static string Notes(TaxDraft draft, TaxApprovalRequest request)
    {
        var signOff =
            $"APPROVED by {request.ReviewedBy}, recorded by taxkeeper on behalf of "
            + $"{request.OperatorName} from {request.NotePath}.\n\n{request.Note.Trim()}";

        return string.IsNullOrWhiteSpace(draft.Notes)
            ? signOff
            : $"{signOff}\n\n--- the drafter's note on version {draft.Version}, kept ---\n"
              + draft.Notes.Trim();
    }

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
