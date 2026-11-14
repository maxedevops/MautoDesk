using System.Globalization;
using Npgsql;

namespace MautoDesk.Compliance.RuleKeeper;

/// <summary>
/// Reads the draft and writes the approval.
/// </summary>
/// <remarks>
/// Raw SQL and one connection. This tool runs as <c>mautodesk_rulekeeper</c>,
/// which holds SELECT on the law tables and INSERT on exactly two of them, so
/// there is nothing for an ORM to map and nothing to track changes to.
/// </remarks>
public sealed class ApprovalStore
{
    private readonly string _connectionString;

    /// <summary>Creates the store over a rule-keeper connection string.</summary>
    public ApprovalStore(string connectionString) => _connectionString = connectionString;

    /// <summary>Opens a connection, so the caller controls the transaction.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Loads the draft, or null when no such version exists.</summary>
    public static async Task<DraftVersion?> LoadDraftAsync(
        NpgsqlConnection connection,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select v.id, v.rule_id, r.rule_code, r.jurisdiction_code, v.version,
                   v.approved_at is not null as is_approved, v.trigger_event, v.severity,
                   v.definition::text, v.source_version_id,
                   sv.content_location, sv.content_hash, s.citation, v.approval_note
              from compliance.regulatory_rule_version v
              join compliance.regulatory_rule r on r.id = v.rule_id
              join compliance.regulatory_source_version sv on sv.id = v.source_version_id
              join compliance.regulatory_source s on s.id = sv.source_id
             where v.id = @id
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("id", versionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DraftVersion
        {
            Id = reader.GetGuid(0),
            RuleId = reader.GetGuid(1),
            RuleCode = reader.GetString(2),
            JurisdictionCode = reader.GetString(3),
            Version = reader.GetString(4),
            IsApproved = reader.GetBoolean(5),
            TriggerEvent = reader.GetString(6),
            Severity = reader.GetString(7),
            DefinitionJson = reader.GetString(8),
            SourceVersionId = reader.GetGuid(9),
            SourceLocation = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false)
                ? null : reader.GetString(10),
            SourceHash = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false)
                ? null : (byte[])reader.GetValue(11),
            SourceCitation = reader.GetString(12),
            ApprovalNote = await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false)
                ? null : reader.GetString(13),
        };
    }

    /// <summary>Loads the draft's stored test cases.</summary>
    public static async Task<List<TestCase>> LoadTestsAsync(
        NpgsqlConnection connection,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select name, expectation, input::text
              from compliance.regulatory_rule_test
             where rule_version_id = @id
             order by name
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("id", versionId);

        var cases = new List<TestCase>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cases.Add(new TestCase(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return cases;
    }

    /// <summary>Loads the already-approved versions of the same rule.</summary>
    public static async Task<List<(DateOnly From, DateOnly? To, string Version)>> LoadApprovedAsync(
        NpgsqlConnection connection,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select effective_from, effective_to, version
              from compliance.regulatory_rule_version
             where rule_id = @rule_id and approved_at is not null
             order by effective_from
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("rule_id", ruleId);

        var approved = new List<(DateOnly, DateOnly?, string)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            approved.Add((
                reader.GetFieldValue<DateOnly>(0),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                    ? null : reader.GetFieldValue<DateOnly>(1),
                reader.GetString(2)));
        }

        return approved;
    }

    /// <summary>The years the jurisdiction has a holiday calendar for.</summary>
    public static async Task<HashSet<int>> LoadHolidayYearsAsync(
        NpgsqlConnection connection,
        string jurisdictionCode,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select distinct extract(year from holiday_on)::int
              from compliance.jurisdiction_holiday
             where jurisdiction_code = @code
            """;

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("code", jurisdictionCode);

        var years = new HashSet<int>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            years.Add(reader.GetInt32(0));
        }

        return years;
    }

    /// <summary>
    /// Writes the approved version and the audit event, together or not at all.
    /// </summary>
    /// <remarks>
    /// One transaction, because an approval nobody can see in the ledger is the
    /// failure mode this whole path exists to avoid. The new row copies the
    /// draft's definition verbatim: the reviewer approved a reading, and the tool
    /// is not entitled to adjust it on the way in.
    /// </remarks>
    public static async Task<Guid> ApproveAsync(
        NpgsqlConnection connection,
        DraftVersion draft,
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var approvedId = Guid.NewGuid();

        const string InsertVersion = """
            insert into compliance.regulatory_rule_version
              (id, rule_id, version, status, effective_from, effective_to, source_version_id,
               trigger_event, definition_schema, definition, severity,
               approved_at, approved_by, approval_note, created_by)
            select @id, v.rule_id, @version, 'approved', @from, @to, v.source_version_id,
                   v.trigger_event, v.definition_schema, v.definition, v.severity,
                   now(), @reviewer, @note, @created_by
              from compliance.regulatory_rule_version v
             where v.id = @draft_id
            """;

        await using (var command = new NpgsqlCommand(InsertVersion, connection, transaction))
        {
            command.Parameters.AddWithValue("id", approvedId);
            command.Parameters.AddWithValue("version", request.Version);
            command.Parameters.AddWithValue("from", request.EffectiveFrom);
            command.Parameters.AddWithValue("to",
                request.EffectiveTo.HasValue ? request.EffectiveTo.Value : DBNull.Value);
            command.Parameters.AddWithValue("reviewer", request.ReviewedBy);
            command.Parameters.AddWithValue("note", ApprovalNote(draft, request));
            command.Parameters.AddWithValue("created_by", $"rulekeeper ({request.OperatorName})");
            command.Parameters.AddWithValue("draft_id", draft.Id);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string InsertAudit = """
            insert into audit.event
              (tenant_id, actor_type, actor_display, access_reason, action,
               entity_schema, entity_type, entity_id, after_state, metadata)
            values
              (null, 'platform_admin', @operator, @reason, 'compliance.rule_version.approved',
               'compliance', 'regulatory_rule_version', @id, @after, @metadata)
            """;

        await using (var command = new NpgsqlCommand(InsertAudit, connection, transaction))
        {
            command.Parameters.AddWithValue("operator", request.OperatorName);
            command.Parameters.AddWithValue("reason",
                $"Approval of {draft.RuleCode} on the written sign-off of {request.ReviewedBy}");
            command.Parameters.AddWithValue("id", approvedId);
            command.Parameters.Add(new NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Jsonb)
            {
                Value = $$"""
                    {
                      "ruleCode": {{Json(draft.RuleCode)}},
                      "version": {{Json(request.Version)}},
                      "approvedBy": {{Json(request.ReviewedBy)}},
                      "effectiveFrom": {{Json(request.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}},
                      "effectiveTo": {{(request.EffectiveTo.HasValue
                          ? Json(request.EffectiveTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                          : "null")}}
                    }
                    """,
            });
            command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlTypes.NpgsqlDbType.Jsonb)
            {
                Value = $$"""
                    {
                      "draftVersionId": {{Json(draft.Id.ToString())}},
                      "draftVersion": {{Json(draft.Version)}},
                      "sourceVersionId": {{Json(draft.SourceVersionId.ToString())}},
                      "sourceLocation": {{Json(draft.SourceLocation ?? string.Empty)}},
                      "signOffFile": {{Json(request.NotePath)}}
                    }
                    """,
            });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return approvedId;
    }

    /// <summary>
    /// The note the approved row carries.
    /// </summary>
    /// <remarks>
    /// The reviewer's sign-off first, then the draft's own note underneath it. The
    /// draft's note is where the <c>INTERPRETATION TO CONFIRM</c> flags live, and
    /// dropping them at approval would erase the record of what the reviewer was
    /// being asked to settle.
    /// </remarks>
    private static string ApprovalNote(DraftVersion draft, ApprovalRequest request)
    {
        var note = $"""
            APPROVED by {request.ReviewedBy}.

            Sign-off recorded from {request.NotePath}, applied by {request.OperatorName}:

            {request.Note}
            """;

        return draft.ApprovalNote is null
            ? note
            : $"""
                {note}

                --- The draft's note as submitted for review ---

                {draft.ApprovalNote}
                """;
    }

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
