using System.Text.Json;
using MautoDesk.SharedKernel;

namespace MautoDesk.Infrastructure.Persistence;

/// <summary>One row of <c>audit.event</c>.</summary>
/// <remarks>
/// <c>PrevHash</c> and <c>Hash</c> are deliberately absent: a BEFORE INSERT
/// trigger computes them from the row's own contents and its predecessor. If the
/// application supplied them, the chain would attest to what the application
/// claimed rather than to what was stored, and a compromised application could
/// write a consistent chain of lies.
/// </remarks>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public Guid EventId { get; set; } = Guid.CreateVersion7();

    public Guid? TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string ActorType { get; set; } = "system";

    public Guid? ActorId { get; set; }

    public string? ActorDisplay { get; set; }

    public string? AccessReason { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? EntitySchema { get; set; }

    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string? BeforeState { get; set; }

    public string? AfterState { get; set; }

    public string Metadata { get; set; } = "{}";

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public Guid? CorrelationId { get; set; }

    /// <summary>Placeholder; the trigger overwrites it. The column is not null.</summary>
    public byte[] Hash { get; set; } = [];
}

/// <summary>
/// Writes ledger entries into the caller's transaction.
/// </summary>
/// <remarks>
/// <see cref="Record"/> only adds to the change tracker. It lands when the
/// handler saves, alongside the state change it describes — so a rolled-back
/// operation leaves no audit entry claiming it happened, and a committed one can
/// never be missing its entry.
/// </remarks>
public sealed class AuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions StateOptions = new(JsonSerializerDefaults.Web);

    private readonly MautoDeskDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IRequestContext _request;
    private readonly IClock _clock;

    public AuditLog(
        MautoDeskDbContext db,
        ITenantContext tenant,
        IRequestContext request,
        IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _request = request;
        _clock = clock;
    }

    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _db.Set<AuditEvent>().Add(new AuditEvent
        {
            TenantId = _tenant.TenantId,
            OccurredAt = _clock.UtcNow,

            // "system" when there is no user: a background job, a migration, or
            // a webhook. Recording it as a user would be a small lie in the one
            // record that exists to be trusted.
            ActorType = _tenant.UserId is null ? "system" : "user",
            ActorId = _tenant.UserId,
            ActorDisplay = _request.ActorDisplay,
            AccessReason = entry.AccessReason,
            Action = entry.Action,
            EntitySchema = entry.EntitySchema,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            BeforeState = Serialize(entry.Before),
            AfterState = Serialize(entry.After),
            Metadata = Serialize(entry.Metadata) ?? "{}",
            IpAddress = _request.IpAddress,
            UserAgent = _request.UserAgent,
            CorrelationId = _request.CorrelationId,
        });
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, StateOptions);
}

/// <summary>
/// The request context outside a request.
/// </summary>
/// <remarks>
/// Everything is null, on purpose. A background job has no address and no
/// browser, and inventing values for them would make the ledger say something
/// that is not true.
/// </remarks>
public sealed class NullRequestContext : IRequestContext
{
    public string? IpAddress => null;

    public string? UserAgent => null;

    public Guid? CorrelationId => null;

    public string? ActorDisplay => null;
}
