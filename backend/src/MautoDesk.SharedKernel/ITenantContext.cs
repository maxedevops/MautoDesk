namespace MautoDesk.SharedKernel;

/// <summary>
/// The tenant and user the current unit of work belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant is resolved from the authenticated principal's <c>tenant</c>
/// claim and from nothing else.</b> Never a header, subdomain, query parameter,
/// or request body. A subdomain may route a request; it may never authorize one.
/// This is ADR-0002 and it is not negotiable.
/// </para>
/// <para>
/// Background jobs re-establish this from the job payload before touching the
/// database, so a worker is subject to exactly the same isolation as a request.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// The current tenant, or <see langword="null"/> when unauthenticated.
    /// </summary>
    /// <remarks>
    /// Null means the database session variable is left unset, which makes every
    /// row-level security predicate evaluate to NULL and therefore deny. No
    /// context yields no rows — never all rows. Fail closed.
    /// </remarks>
    public Guid? TenantId { get; }

    public Guid? UserId { get; }

    /// <summary>Effective permission codes, e.g. <c>inventory.cost.read</c>.</summary>
    public IReadOnlySet<string> Permissions { get; }

    public bool IsAuthenticated { get; }

    /// <summary>The tenant, or an exception if there isn't one.</summary>
    /// <exception cref="InvalidOperationException">No tenant is in scope.</exception>
    public Guid RequireTenantId() => TenantId
        ?? throw new InvalidOperationException(
            "No tenant is in scope. A tenant-scoped operation ran outside an authenticated " +
            "request or a job that restored its tenant context.");

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}

/// <summary>
/// A mutable tenant scope, used by background jobs and integration tests.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ITenantContext"/>: request handling
/// consumes the read-only interface and has no way to reassign the tenant
/// mid-request, which removes a whole class of confused-deputy bug.
/// </remarks>
public interface ITenantScopeSetter
{
    public void SetScope(Guid tenantId, Guid? userId = null, IReadOnlySet<string>? permissions = null);

    public void Clear();
}
