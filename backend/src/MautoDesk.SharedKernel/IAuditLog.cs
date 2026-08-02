namespace MautoDesk.SharedKernel;

/// <summary>
/// One thing that happened, in the words an auditor would use.
/// </summary>
/// <remarks>
/// <para>
/// <c>Action</c> is a dotted, past-tense code — <c>inventory.vehicle.published</c>
/// — so the ledger can be filtered by what happened rather than by prose.
/// </para>
/// <para>
/// <c>Before</c> and <c>After</c> are serialized as JSON. Record the fields that
/// changed, not the whole entity: an auditor asking "who changed this price?"
/// wants two numbers, and a full entity snapshot on every write turns the ledger
/// into a second copy of the database — including a copy of every piece of
/// customer information the retention policy is supposed to govern.
/// </para>
/// </remarks>
public sealed record AuditEntry
{
    public required string Action { get; init; }

    public string? EntitySchema { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    public object? Before { get; init; }

    public object? After { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Why a platform administrator was in a tenant's data. Required for that actor.</summary>
    public string? AccessReason { get; init; }
}

/// <summary>
/// The tamper-evident ledger.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording is not a separate write.</b> The entry joins the caller's unit
/// of work and commits in the same transaction as the change it describes, so
/// there is no state in which the change landed and the audit did not. That is
/// the whole difference between an audit trail and a log file.
/// </para>
/// <para>
/// Rows are append-only — a database trigger blocks update and delete, and the
/// application role's grants say the same thing. A correction is a new entry.
/// </para>
/// </remarks>
public interface IAuditLog
{
    public void Record(AuditEntry entry);
}

/// <summary>
/// The bits of the caller a ledger entry needs and the domain has no business knowing.
/// </summary>
/// <remarks>
/// Implemented at the edge, where an HTTP request exists. A background job binds
/// an implementation whose values are null, which is honest: there was no
/// browser, no address, and no session.
/// </remarks>
public interface IRequestContext
{
    public string? IpAddress { get; }

    public string? UserAgent { get; }

    /// <summary>Ties every entry from one request together, and to the logs.</summary>
    public Guid? CorrelationId { get; }

    /// <summary>A human-readable actor, so the ledger stays readable after a user is deleted.</summary>
    public string? ActorDisplay { get; }
}
