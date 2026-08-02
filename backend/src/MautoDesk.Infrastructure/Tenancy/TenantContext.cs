using MautoDesk.SharedKernel;

namespace MautoDesk.Infrastructure.Tenancy;

/// <summary>
/// The ambient tenant scope for the current request or job.
/// </summary>
/// <remarks>
/// Registered as scoped, so it is per-request in the API and per-job in the
/// worker. It implements both the read-only <see cref="ITenantContext"/> that
/// handlers consume and the <see cref="ITenantScopeSetter"/> that only the
/// authentication middleware and the job runner resolve — a handler that
/// depends on <see cref="ITenantContext"/> has no way to reassign the tenant
/// mid-request.
/// </remarks>
public sealed class TenantContext : ITenantContext, ITenantScopeSetter
{
    private static readonly IReadOnlySet<string> NoPermissions =
        new HashSet<string>(StringComparer.Ordinal);

    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public IReadOnlySet<string> Permissions { get; private set; } = NoPermissions;

    public bool IsAuthenticated => TenantId is not null;

    public void SetScope(Guid tenantId, Guid? userId = null, IReadOnlySet<string>? permissions = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Guid.Empty is not a tenant. Passing it would set a session variable that looks " +
                "valid but matches no row, which is harder to diagnose than an outright failure.",
                nameof(tenantId));
        }

        TenantId = tenantId;
        UserId = userId;
        Permissions = permissions ?? NoPermissions;
    }

    public void Clear()
    {
        TenantId = null;
        UserId = null;
        Permissions = NoPermissions;
    }
}

/// <summary>The system clock.</summary>
/// <remarks>
/// The only place in the codebase permitted to read the machine clock; an
/// architecture test enforces that. Everything else takes <see cref="IClock"/>.
/// </remarks>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
