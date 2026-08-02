namespace MautoDesk.SharedKernel;

/// <summary>
/// Something that happened in the domain, worth telling other modules about.
/// </summary>
/// <remarks>
/// Raised by an aggregate, collected by the unit of work, and written to
/// <c>app.outbox_message</c> in the SAME transaction as the state change
/// (ADR-0006). That transactional coupling is what makes the constitution's
/// "enter data once, it flows everywhere" guarantee hold under failure: a
/// vehicle that saved always eventually publishes, even if the process dies
/// immediately afterwards.
/// </remarks>
public interface IDomainEvent
{
    public Guid EventId { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>Stable wire name, e.g. <c>inventory.vehicle.created</c>.</summary>
    public string EventType { get; }
}

/// <summary>Base class for domain events.</summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; }

    public abstract string EventType { get; }
}

/// <summary>An entity with identity.</summary>
public abstract class Entity
{
    protected Entity(Guid id) => Id = id;

    /// <summary>Required by EF Core materialization.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected set; }

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id && Id != Guid.Empty;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// An entity that owns a consistency boundary and may raise domain events.
/// </summary>
/// <remarks>
/// Every aggregate in this system is tenant-owned. <see cref="TenantId"/> is set
/// once at construction and is never mutable — the database enforces the same
/// rule with a trigger, because a row that can change tenants is a cross-tenant
/// leak waiting to happen.
/// </remarks>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id, Guid tenantId)
        : base(id)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An aggregate cannot exist outside a tenant.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    /// <summary>Required by EF Core materialization.</summary>
    protected AggregateRoot()
    {
    }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public Guid? CreatedBy { get; protected set; }

    public DateTimeOffset UpdatedAt { get; protected set; }

    public Guid? UpdatedBy { get; protected set; }

    public DateTimeOffset? DeletedAt { get; protected set; }

    public Guid? DeletedBy { get; protected set; }

    /// <summary>
    /// Soft-deleted. Note this is NOT erasure — the row survives for audit and
    /// statutory retention. See docs/03-database-design.md §7.
    /// </summary>
    public bool IsDeleted => DeletedAt is not null;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>
/// The clock. Injected, never called statically.
/// </summary>
/// <remarks>
/// An architecture test fails the build on any use of <c>DateTime.Now</c> or
/// <c>DateTime.UtcNow</c> outside the implementation of this interface. Time is
/// an input: a deal that computes differently at 23:59 than at 00:01 must be
/// testable at both, and "flaky at midnight" is not an acceptable property for
/// software that prices contracts.
/// </remarks>
public interface IClock
{
    public DateTimeOffset UtcNow { get; }

    public DateOnly Today { get; }
}
