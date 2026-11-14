using System.Data;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Infrastructure.Persistence;

/// <summary>
/// <see cref="IIdempotencyStore"/> over <c>app.idempotency_key</c>.
/// </summary>
/// <remarks>
/// <para>
/// Goes through the request's <see cref="MautoDeskDbContext"/> connection rather
/// than opening its own. That is not a convenience: opening the connection
/// through EF runs <see cref="Tenancy.TenantConnectionInterceptor"/>, which sets
/// <c>app.tenant_id</c>, which is what makes the row-level security policy on
/// <c>app.idempotency_key</c> apply. A raw <see cref="NpgsqlConnection"/> opened
/// here would carry no tenant scope and read nothing — or, worse, would need a
/// role that could.
/// </para>
/// <para>
/// Written with <see cref="NpgsqlCommand"/> rather than EF's raw-SQL helpers
/// because both of those have sharp edges this code would hit: the
/// <c>ExecuteSqlRaw</c> overload takes <c>IEnumerable&lt;object&gt;</c> and
/// rejects <see cref="DBNull"/>, and <c>SqlQueryRaw</c> requires scalar columns
/// to be named <c>Value</c>. Neither is worth working around for four statements.
/// </para>
/// </remarks>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly MautoDeskDbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public IdempotencyStore(MautoDeskDbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement, and the atomicity lives in the database. <c>ON CONFLICT DO
    /// NOTHING ... RETURNING</c> returns a row only when the insert actually
    /// happened, so "did I win the race" is answered by whether anything came
    /// back — not by a prior SELECT that another request could invalidate
    /// between the read and the write.
    /// </remarks>
    public async Task<IdempotencyReservation> TryReserveAsync(
        string key,
        string endpoint,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestHash);

        var tenantId = _tenantContext.RequireTenantId();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        const string ReserveSql = """
            insert into app.idempotency_key (tenant_id, key, endpoint, request_hash)
            values (@tenant_id, @key, @endpoint, @request_hash)
            on conflict (tenant_id, key, endpoint) do nothing
            returning 1
            """;

        await using (var reserve = connection.CreateCommand())
        {
            reserve.CommandText = ReserveSql;
            reserve.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
            reserve.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = key });
            reserve.Parameters.Add(new NpgsqlParameter("endpoint", NpgsqlDbType.Text) { Value = endpoint });
            reserve.Parameters.Add(new NpgsqlParameter("request_hash", NpgsqlDbType.Bytea) { Value = requestHash });

            var inserted = await reserve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (inserted is not null)
            {
                return new IdempotencyReservation(IdempotencyOutcome.Reserved, null);
            }
        }

        // Somebody else holds the key. What they did with it decides the answer.
        const string ExistingSql = """
            select request_hash, completed_at, response_status, response_body::text
              from app.idempotency_key
             where tenant_id = @tenant_id and key = @key and endpoint = @endpoint
            """;

        await using var existing = connection.CreateCommand();
        existing.CommandText = ExistingSql;
        existing.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
        existing.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = key });
        existing.Parameters.Add(new NpgsqlParameter("endpoint", NpgsqlDbType.Text) { Value = endpoint });

        await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The row vanished between the failed insert and this read — a prune
            // ran in the gap. Treat it as a fresh request rather than inventing a
            // conflict; the caller will re-reserve on its next attempt.
            return new IdempotencyReservation(IdempotencyOutcome.Reserved, null);
        }

        var storedHash = (byte[])reader["request_hash"];

        // Fixed-time comparison. The hash is not a secret, but a length-and-
        // content comparison that short-circuits is a habit worth not forming in
        // a file that decides whether two requests are the same.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(storedHash, requestHash))
        {
            return new IdempotencyReservation(IdempotencyOutcome.Mismatch, null);
        }

        if (await reader.IsDBNullAsync(reader.GetOrdinal("completed_at"), cancellationToken)
            .ConfigureAwait(false))
        {
            return new IdempotencyReservation(IdempotencyOutcome.InProgress, null);
        }

        var statusOrdinal = reader.GetOrdinal("response_status");
        var bodyOrdinal = reader.GetOrdinal("response_body");

        var status = await reader.IsDBNullAsync(statusOrdinal, cancellationToken).ConfigureAwait(false)
            ? StatusCodesNoContent
            : reader.GetInt32(statusOrdinal);

        var body = await reader.IsDBNullAsync(bodyOrdinal, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(bodyOrdinal);

        return new IdempotencyReservation(
            IdempotencyOutcome.Replay,
            new IdempotentResponse(status, body));
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        string key,
        string endpoint,
        int status,
        string? body,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        const string Sql = """
            update app.idempotency_key
               set response_status = @status,
                   response_body   = @body,
                   completed_at    = now()
             where tenant_id = @tenant_id and key = @key and endpoint = @endpoint
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = key });
        command.Parameters.Add(new NpgsqlParameter("endpoint", NpgsqlDbType.Text) { Value = endpoint });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = status });
        // Json, not Jsonb. The column is json so the replayed body is the
        // original bytes rather than a re-serialization of the same object.
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Json)
        {
            Value = (object?)body ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string endpoint, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // Only an incomplete reservation is released. A completed one is a
        // recorded answer, and deleting it here would let a later retry produce a
        // second, different one.
        const string Sql = """
            delete from app.idempotency_key
             where tenant_id = @tenant_id and key = @key and endpoint = @endpoint
               and completed_at is null
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = key });
        command.Parameters.Add(new NpgsqlParameter("endpoint", NpgsqlDbType.Text) { Value = endpoint });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const int StatusCodesNoContent = 204;

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            // Through EF, so the tenant interceptor runs and RLS has something to
            // compare against.
            await _dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
