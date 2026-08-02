using System.Data.Common;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MautoDesk.Infrastructure.Tenancy;

/// <summary>
/// Stamps the tenant onto every database connection so PostgreSQL row-level
/// security can enforce isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the single most security-critical class in the codebase.</b> Every
/// RLS policy in <c>db/migrations/V0006__rls_and_grants.sql</c> compares
/// <c>tenant_id</c> against <c>app.current_tenant_id()</c>, which reads the
/// <c>app.tenant_id</c> session variable this class sets. If it sets the wrong
/// tenant, one dealership reads another's customers. If it fails to reset,
/// a pooled connection carries one request's tenant into the next.
/// </para>
/// <para>
/// <b>Why RESET rather than transaction-local <c>set_config</c>.</b> The obvious
/// implementation is <c>set_config('app.tenant_id', $1, true)</c> — the
/// <c>true</c> making it transaction-local and therefore self-cleaning. That is
/// correct only inside an explicit transaction; EF Core executes plenty of
/// reads with no transaction open, and outside one, a "transaction-local"
/// setting silently applies to just that statement and then vanishes, so the
/// very next query in the same request sees no tenant and returns nothing.
/// </para>
/// <para>
/// So the setting is made session-local, and correctness depends on clearing it
/// when the connection returns to the pool. Both halves are implemented here:
/// <see cref="ConnectionOpenedAsync"/> sets it, and
/// <see cref="ConnectionClosingAsync"/> resets it. The reset is the half that is
/// easy to forget and impossible to notice in manual testing, which is why
/// <c>PooledConnectionLeakTests</c> exercises exactly that path.
/// </para>
/// <para>
/// <b>Fail-closed.</b> With no tenant in scope the variable is set to the empty
/// string, so <c>app.current_tenant_id()</c> returns NULL, every RLS predicate
/// evaluates to NULL, and the query returns zero rows. No context yields no
/// data — never all data.
/// </para>
/// </remarks>
public sealed partial class TenantConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// Sets both session variables in one round trip.
    /// </summary>
    /// <remarks>
    /// A <c>const</c>, and the only string ever assigned to
    /// <c>CommandText</c> in this class. Values arrive exclusively through
    /// <see cref="NpgsqlParameter"/>; nothing is ever concatenated or
    /// interpolated into this statement. Clearing the scope is the same
    /// statement with empty strings, which is why there is one SQL constant
    /// rather than two.
    /// </remarks>
    private const string SetScopeSql =
        "select set_config('app.tenant_id', @tenant_id, false), " +
        "set_config('app.user_id', @user_id, false)";

    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantConnectionInterceptor> _logger;

    public TenantConnectionInterceptor(
        ITenantContext tenantContext,
        ILogger<TenantConnectionInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyScopeAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyScopeAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    /// <summary>
    /// Clears the tenant before the connection goes back to the pool.
    /// </summary>
    /// <remarks>
    /// Without this, connection #7 handling tenant A's request would still carry
    /// <c>app.tenant_id = A</c> when it is later handed to a request for tenant
    /// B. B's own scope is applied on open, so the window is narrow — but a
    /// connection reused without a fresh open, or any code path that reads before
    /// the interceptor runs, would read A's rows. Narrow is not zero.
    /// </remarks>
    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        await ClearScopeAsync(connection, CancellationToken.None).ConfigureAwait(false);
        return await base.ConnectionClosingAsync(connection, eventData, result).ConfigureAwait(false);
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        ClearScopeAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        return base.ConnectionClosing(connection, eventData, result);
    }

    private async Task ApplyScopeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId;
        var userId = _tenantContext.UserId;

        // is_local = false: session-scoped, so it survives across the several
        // statements EF issues for one logical operation, including those
        // outside an explicit transaction.
        await ExecuteScopeAsync(
            connection,
            tenantId?.ToString() ?? string.Empty,
            userId?.ToString() ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

        if (tenantId is null)
        {
            // Not an error — health checks, migrations and the login path all run
            // without a tenant. Logged at debug because a flood of these on a
            // tenant-scoped endpoint is a useful signal that something is wrong.
            LogNoTenantScope(_logger);
        }
    }

    /// <summary>Resets both variables by setting them to the empty string.</summary>
    private static async Task ClearScopeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await ExecuteScopeAsync(connection, string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The connection is already broken, so there is nothing to leak — a
            // broken connection is not returned to the pool. Swallowing here keeps
            // the original failure visible instead of masking it with a secondary
            // failure raised during teardown.
        }
    }

    private static async Task ExecuteScopeAsync(
        DbConnection connection,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SetScopeSql;

        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "tenant_id";
        tenantParameter.Value = tenantId;
        command.Parameters.Add(tenantParameter);

        var userParameter = command.CreateParameter();
        userParameter.ParameterName = "user_id";
        userParameter.Value = userId;
        command.Parameters.Add(userParameter);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Database connection opened with no tenant scope; row-level security will deny all rows.")]
    private static partial void LogNoTenantScope(ILogger logger);
}
