using System.Data;
using System.Globalization;
using MautoDesk.Compliance.Application;
using MautoDesk.Compliance.Contracts;
using MautoDesk.Compliance.Domain;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Compliance.Infrastructure;

/// <summary>
/// Reads regulatory rules.
/// </summary>
/// <remarks>
/// Raw SQL rather than EF entities. These tables are shared reference data, they
/// are read-only to the application role, and the shapes are read once into
/// immutable records — an ORM mapping would add a change tracker to data nothing
/// is allowed to change.
/// </remarks>
public sealed class RegulatoryRuleStore : IRegulatoryRuleStore
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the store.</summary>
    public RegulatoryRuleStore(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegulatoryRuleRow>> FindApplicableAsync(
        string jurisdictionCode,
        string triggerEvent,
        DateOnly onDate,
        CancellationToken cancellationToken)
    {
        // approved_at is not null is repeated here even though the evaluator checks
        // it too. Belt and braces on the one filter whose absence would mean
        // evaluating a draft against a real deal.
        const string Sql = """
            select v.id, r.rule_code, v.version, r.jurisdiction_code, v.trigger_event,
                   v.severity, v.status, v.effective_from, v.effective_to,
                   v.approved_at is not null as is_approved, v.source_version_id,
                   v.definition::text, coalesce(v.approval_note, ''), s.citation
              from compliance.regulatory_rule_version v
              join compliance.regulatory_rule r on r.id = v.rule_id
              join compliance.regulatory_source_version sv on sv.id = v.source_version_id
              join compliance.regulatory_source s on s.id = sv.source_id
             where r.jurisdiction_code = @jurisdiction
               and v.trigger_event = @trigger
               and v.approved_at is not null
               and v.status in ('approved','scheduled','active')
               and v.effective_from <= @on_date
               and (v.effective_to is null or v.effective_to > @on_date)
             order by r.rule_code
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("jurisdiction", NpgsqlDbType.Text)
        {
            Value = jurisdictionCode,
        });
        command.Parameters.Add(new NpgsqlParameter("trigger", NpgsqlDbType.Text) { Value = triggerEvent });
        command.Parameters.Add(new NpgsqlParameter("on_date", NpgsqlDbType.Date) { Value = onDate });

        var rows = new List<RegulatoryRuleRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RegulatoryRuleRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                DateOnly.FromDateTime(reader.GetDateTime(7)),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                    ? null
                    : DateOnly.FromDateTime(reader.GetDateTime(8)),
                reader.GetBoolean(9),
                reader.GetGuid(10),
                reader.GetString(11),
                reader.GetString(12),
                await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(13)));
        }

        return rows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The pack version is derived from the newest effective date among the rules
    /// in force, so it changes exactly when the applicable set does. A hand-kept
    /// version number would be one more thing to forget to bump.
    /// </remarks>
    /// <inheritdoc />
    public async Task<bool> JurisdictionExistsAsync(
        string jurisdictionCode,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select exists (
              select 1 from compliance.regulatory_jurisdiction where code = @jurisdiction
            )
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("jurisdiction", NpgsqlDbType.Text)
        {
            Value = jurisdictionCode,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is true;
    }

    public async Task<string> ResolvePackVersionAsync(
        string jurisdictionCode,
        DateOnly onDate,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select count(*)::int, coalesce(max(v.effective_from)::text, 'none')
              from compliance.regulatory_rule_version v
              join compliance.regulatory_rule r on r.id = v.rule_id
             where r.jurisdiction_code = @jurisdiction
               and v.approved_at is not null
               and v.status in ('approved','scheduled','active')
               and v.effective_from <= @on_date
               and (v.effective_to is null or v.effective_to > @on_date)
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("jurisdiction", NpgsqlDbType.Text)
        {
            Value = jurisdictionCode,
        });
        command.Parameters.Add(new NpgsqlParameter("on_date", NpgsqlDbType.Date) { Value = onDate });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return $"{jurisdictionCode}-empty";
        }

        var count = reader.GetInt32(0);
        var newest = reader.GetString(1);

        return string.Create(CultureInfo.InvariantCulture, $"{jurisdictionCode}-{newest}-{count}");
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}

/// <summary>Reads a jurisdiction's holiday calendar.</summary>
public sealed class HolidayCalendarStore : IHolidayCalendarStore
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the store.</summary>
    public HolidayCalendarStore(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateOnly>> GetHolidaysAsync(
        string jurisdictionCode,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select holiday_on from compliance.jurisdiction_holiday
             where jurisdiction_code = @jurisdiction
            """;

        var connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("jurisdiction", NpgsqlDbType.Text)
        {
            Value = jurisdictionCode,
        });

        var holidays = new List<DateOnly>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            holidays.Add(DateOnly.FromDateTime(reader.GetDateTime(0)));
        }

        return holidays;
    }
}

/// <summary>
/// Writes compliance runs and their verdicts.
/// </summary>
/// <remarks>
/// Both tables are append-only by trigger, so this only ever inserts. The run and
/// its results go in one transaction: a run with no results, or results with no
/// run, would each be a lie about what the engine decided.
/// </remarks>
public sealed class ComplianceRunStore : IComplianceRunStore
{
    private readonly MautoDeskDbContext _db;
    private readonly IClock _clock;

    /// <summary>Creates the store.</summary>
    public ComplianceRunStore(MautoDeskDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        Guid runId,
        Guid tenantId,
        Guid dealId,
        ComplianceRun run,
        string inputJson,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Through EF rather than connection.BeginTransactionAsync(). The raw ADO
        // call starts a transaction the context knows nothing about, on a
        // connection the context owns — so EF's next command runs inside a
        // transaction it did not open and cannot roll back. Going through
        // Database.BeginTransactionAsync keeps the two in agreement.
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var connection = _db.Database.GetDbConnection();

        const string RunSql = """
            insert into compliance.compliance_run
              (id, tenant_id, deal_id, jurisdiction_code, trigger_event, engine_version,
               rule_pack_version, evaluated_on, executed_at, executed_by, overall_status,
               blocking_count, warning_count, input_hash, result_hash, input)
            values
              (@id, @tenant_id, @deal_id, @jurisdiction, @trigger, @engine_version,
               @pack_version, @evaluated_on, @executed_at, @executed_by, @overall_status,
               @blocking, @warning, @input_hash, @result_hash, @input)
            """;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = RunSql;
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = runId });
            command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
            command.Parameters.Add(new NpgsqlParameter("deal_id", NpgsqlDbType.Uuid) { Value = dealId });
            command.Parameters.Add(new NpgsqlParameter("jurisdiction", NpgsqlDbType.Text)
            {
                Value = run.JurisdictionCode,
            });
            command.Parameters.Add(new NpgsqlParameter("trigger", NpgsqlDbType.Text)
            {
                Value = run.TriggerEvent,
            });
            command.Parameters.Add(new NpgsqlParameter("engine_version", NpgsqlDbType.Text)
            {
                Value = run.EngineVersion,
            });
            command.Parameters.Add(new NpgsqlParameter("pack_version", NpgsqlDbType.Text)
            {
                Value = run.RulePackVersion,
            });
            command.Parameters.Add(new NpgsqlParameter("evaluated_on", NpgsqlDbType.Date)
            {
                Value = run.EvaluatedOn,
            });
            command.Parameters.Add(new NpgsqlParameter("executed_at", NpgsqlDbType.TimestampTz)
            {
                Value = _clock.UtcNow,
            });
            command.Parameters.Add(new NpgsqlParameter("executed_by", NpgsqlDbType.Uuid)
            {
                Value = (object?)userId ?? DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("overall_status", NpgsqlDbType.Text)
            {
                Value = run.OverallStatus.ToString().ToLowerInvariant(),
            });
            command.Parameters.Add(new NpgsqlParameter("blocking", NpgsqlDbType.Integer)
            {
                Value = run.BlockingCount,
            });
            command.Parameters.Add(new NpgsqlParameter("warning", NpgsqlDbType.Integer)
            {
                Value = run.WarningCount,
            });
            command.Parameters.Add(new NpgsqlParameter("input_hash", NpgsqlDbType.Bytea)
            {
                Value = run.InputHash,
            });
            command.Parameters.Add(new NpgsqlParameter("result_hash", NpgsqlDbType.Bytea)
            {
                Value = run.ResultHash,
            });

            // The canonical facts, stored as a JSON string rather than reformatted
            // into an object: the hash was taken over exactly this text, so storing
            // anything else would break the reproduction it exists for.
            command.Parameters.Add(new NpgsqlParameter("input", NpgsqlDbType.Jsonb)
            {
                Value = System.Text.Json.JsonSerializer.Serialize(new { canonical = inputJson }),
            });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string ResultSql = """
            insert into compliance.compliance_result
              (tenant_id, run_id, rule_code, rule_version, rule_version_id, source_version_id,
               status, severity, message, evaluation_detail, sort_order)
            values
              (@tenant_id, @run_id, @rule_code, @rule_version, @rule_version_id, @source_version_id,
               @status, @severity, @message, @detail, @sort_order)
            """;

        var order = 0;

        foreach (var result in run.Results)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = ResultSql;
            command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
            command.Parameters.Add(new NpgsqlParameter("run_id", NpgsqlDbType.Uuid) { Value = runId });
            command.Parameters.Add(new NpgsqlParameter("rule_code", NpgsqlDbType.Text)
            {
                Value = result.RuleCode,
            });
            command.Parameters.Add(new NpgsqlParameter("rule_version", NpgsqlDbType.Text)
            {
                Value = result.RuleVersion,
            });
            command.Parameters.Add(new NpgsqlParameter("rule_version_id", NpgsqlDbType.Uuid)
            {
                Value = result.RuleVersionId,
            });
            command.Parameters.Add(new NpgsqlParameter("source_version_id", NpgsqlDbType.Uuid)
            {
                Value = result.SourceVersionId,
            });
            command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
            {
                Value = result.Status.ToString().ToLowerInvariant(),
            });
            command.Parameters.Add(new NpgsqlParameter("severity", NpgsqlDbType.Text)
            {
                Value = result.Severity.ToString().ToLowerInvariant(),
            });
            command.Parameters.Add(new NpgsqlParameter("message", NpgsqlDbType.Text)
            {
                Value = result.Message,
            });
            command.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Jsonb)
            {
                Value = System.Text.Json.JsonSerializer.Serialize(new
                {
                    requirementCode = result.RequirementCode,
                    deadline = result.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    conditions = result.EvaluationDetail ?? [],
                }),
            });
            command.Parameters.Add(new NpgsqlParameter("sort_order", NpgsqlDbType.Integer)
            {
                Value = order++,
            });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Flushes whatever the caller tracked — in practice the audit entry naming
        // this run — inside the same transaction. Either both land or neither
        // does, which is the guarantee IAuditLog promises and could not keep while
        // this handler had no unit of work of its own.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ComplianceRunDto?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        ReadRunAsync(
            """
            select id, deal_id, trigger_event, jurisdiction_code, evaluated_on, engine_version,
                   rule_pack_version, overall_status, blocking_count, warning_count, executed_at,
                   input_hash, result_hash
              from compliance.compliance_run
             where id = @key
            """,
            runId,
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Ordered by <c>executed_at</c> and then by <c>id</c>. The tiebreak is not
    /// decoration: two runs written in the same microsecond would otherwise come
    /// back in whatever order the planner chose, and "the latest evaluation" would
    /// be a coin toss. Ids are UUIDv7, so ordering by id is ordering by time.
    /// </remarks>
    public Task<ComplianceRunDto?> GetLatestAsync(Guid dealId, CancellationToken cancellationToken) =>
        ReadRunAsync(
            """
            select id, deal_id, trigger_event, jurisdiction_code, evaluated_on, engine_version,
                   rule_pack_version, overall_status, blocking_count, warning_count, executed_at,
                   input_hash, result_hash
              from compliance.compliance_run
             where deal_id = @key
             order by executed_at desc, id desc
             limit 1
            """,
            dealId,
            cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both statements are literals declared above; the only value is bound.")]
    private async Task<ComplianceRunDto?> ReadRunAsync(
        string runSql,
        Guid key,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid runId;
        ComplianceRunDto shell;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = runSql;
            command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Uuid) { Value = key });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            runId = reader.GetGuid(0);

            shell = new ComplianceRunDto(
                runId,
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                DateOnly.FromDateTime(reader.GetDateTime(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                [],
                Convert.ToHexString((byte[])reader[11]),
                Convert.ToHexString((byte[])reader[12]));
        }

        var results = await ReadResultsAsync(connection, runId, cancellationToken).ConfigureAwait(false);

        return shell with { Results = results };
    }

    private static async Task<List<ComplianceResultDto>> ReadResultsAsync(
        System.Data.Common.DbConnection connection,
        Guid runId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select rule_code, rule_version, status, severity, message,
                   evaluation_detail->>'requirementCode',
                   evaluation_detail->>'deadline',
                   evaluation_detail->'conditions'
              from compliance.compliance_result
             where run_id = @run_id
             order by sort_order
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("run_id", NpgsqlDbType.Uuid) { Value = runId });

        var results = new List<ComplianceResultDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var deadline = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? (DateOnly?)null
                : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            var conditions = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [];

            results.Add(new ComplianceResultDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetString(5),
                deadline,
                null,
                conditions));
        }

        return results;
    }
}
