using MautoDesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MautoDesk.Api;

/// <summary>
/// Readiness check for the database.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-written rather than <c>AddDbContextCheck</c>, because
/// "can I open a connection" is not the question that matters here. The question
/// is whether we are connected <b>as the right role</b>: the whole tenant
/// isolation design rests on the application connecting as a role that has no
/// <c>BYPASSRLS</c> and is not the table owner. A deployment misconfigured to
/// use a superuser connection string would pass a plain connectivity check
/// while silently disabling every row-level security policy in the system.
/// </para>
/// <para>
/// So this check fails loudly on a superuser connection. That is a deliberate
/// choice to make a catastrophic misconfiguration impossible to deploy quietly.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly MautoDeskDbContext _db;

    public DatabaseHealthCheck(MautoDeskDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await _db.Database
                .SqlQueryRaw<RoleInfo>(
                    """
                    select current_user as "CurrentUser",
                           rolsuper as "IsSuperuser",
                           rolbypassrls as "CanBypassRls"
                      from pg_roles
                     where rolname = current_user
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var role = rows.FirstOrDefault();

            if (role is null)
            {
                return HealthCheckResult.Unhealthy("Could not determine the connected database role.");
            }

            if (role.IsSuperuser || role.CanBypassRls)
            {
                return HealthCheckResult.Unhealthy(
                    $"Connected as '{role.CurrentUser}', which can bypass row-level security. " +
                    "Tenant isolation is NOT enforced on this connection. Use the mautodesk_app role.");
            }

            return HealthCheckResult.Healthy($"Connected as '{role.CurrentUser}' with RLS enforced.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy("The database is not reachable.", ex);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("The database is not reachable.", ex);
        }
    }

    private sealed record RoleInfo(string CurrentUser, bool IsSuperuser, bool CanBypassRls);
}
