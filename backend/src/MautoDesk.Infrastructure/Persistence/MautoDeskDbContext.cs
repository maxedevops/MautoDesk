using System.Reflection;
using System.Text.Json;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace MautoDesk.Infrastructure.Persistence;

/// <summary>
/// Marks a module's Infrastructure assembly so its EF configurations are picked up.
/// </summary>
/// <remarks>
/// One <see cref="DbContext"/> serves every module. That is deliberate: the
/// modular monolith of ADR-0001 gets its isolation from project references and
/// architecture tests, not from separate connections. A single context means a
/// single transaction, which is what lets a state change and its outbox row
/// commit atomically.
/// </remarks>
public interface IModuleSchema
{
    public Assembly ConfigurationAssembly { get; }
}

/// <summary>A message awaiting publication, written with the state it describes.</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }

    public Guid MessageId { get; set; }

    public Guid? TenantId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    public Guid? CorrelationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>The application's database context.</summary>
public sealed class MautoDeskDbContext : DbContext
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IModuleSchema> _modules;
    private readonly ITenantContext _tenantContext;
    private readonly IClock _clock;

    public MautoDeskDbContext(
        DbContextOptions<MautoDeskDbContext> options,
        IEnumerable<IModuleSchema> modules,
        ITenantContext tenantContext,
        IClock clock)
        : base(options)
    {
        _modules = modules;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_message", "app");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            entity.Property(e => e.AvailableAt).HasColumnName("available_at");
            entity.Property(e => e.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.LastError).HasColumnName("last_error");
        });

        foreach (var module in _modules)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(module.ConfigurationAssembly);
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Saves changes, stamping audit fields and writing domain events to the outbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox rows are added to the same change tracker as the state they
    /// describe, so EF emits them in one transaction. Either the vehicle and its
    /// <c>inventory.vehicle.created</c> event both land, or neither does. There
    /// is no window in which a vehicle exists but nothing downstream was told.
    /// </para>
    /// <para>
    /// Note the tenant on each outbox row is taken from the aggregate, not from
    /// the ambient context. They should agree, but the aggregate is the fact and
    /// the context is the assertion — and a job that publishes to the wrong
    /// tenant is exactly the failure this system exists to prevent.
    /// </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        WriteOutboxMessages();
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StampAuditFields()
    {
        var now = _clock.UtcNow;
        var userId = _tenantContext.UserId;

        foreach (var entry in ChangeTracker.Entries<AggregateRoot>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    Set(entry, nameof(AggregateRoot.CreatedAt), now);
                    Set(entry, nameof(AggregateRoot.CreatedBy), userId);
                    Set(entry, nameof(AggregateRoot.UpdatedAt), now);
                    Set(entry, nameof(AggregateRoot.UpdatedBy), userId);
                    break;

                case EntityState.Modified:
                    Set(entry, nameof(AggregateRoot.UpdatedAt), now);
                    Set(entry, nameof(AggregateRoot.UpdatedBy), userId);

                    // CreatedAt/CreatedBy are immutable once written. Without this
                    // a detached-then-attached aggregate would silently rewrite
                    // its own provenance, which an auditor would rightly query.
                    Freeze(entry, nameof(AggregateRoot.CreatedAt));
                    Freeze(entry, nameof(AggregateRoot.CreatedBy));
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Sets an audit property, if the aggregate actually maps it.
    /// </summary>
    /// <remarks>
    /// Not every aggregate carries the full audit column set —
    /// <c>identity.session</c>, for instance, has no <c>updated_at</c> or
    /// <c>created_by</c>, because a session is written once and revoked, never
    /// edited. Probing the model rather than assuming keeps the stamper from
    /// throwing on those, without weakening it for the aggregates that do have
    /// the columns.
    /// </remarks>
    private static void Set(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName,
        object? value)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).CurrentValue = value;
        }
    }

    private static void Freeze(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).IsModified = false;
        }
    }

    private void WriteOutboxMessages()
    {
        var aggregates = ChangeTracker.Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        var now = _clock.UtcNow;

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = domainEvent.EventId,
                    TenantId = aggregate.TenantId,
                    EventType = domainEvent.EventType,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), PayloadOptions),
                    OccurredAt = domainEvent.OccurredAt == default ? now : domainEvent.OccurredAt,
                    AvailableAt = now,
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
