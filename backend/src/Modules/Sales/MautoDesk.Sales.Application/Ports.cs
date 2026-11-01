using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Application;

/// <summary>Persistence for the <see cref="Deal"/> aggregate.</summary>
/// <remarks>
/// Implicitly tenant-scoped, like every other repository here: the connection
/// carries <c>app.tenant_id</c> and row-level security filters underneath. A
/// query for another dealer's deal returns nothing, which surfaces as a 404.
/// </remarks>
public interface IDealRepository
{
    /// <summary>Loads a deal, or null when it does not exist for this tenant.</summary>
    public Task<Deal?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when the tenant already has a deal with this number.</summary>
    public Task<bool> DealNumberExistsAsync(string dealNumber, CancellationToken cancellationToken);

    /// <summary>The next unused number in the tenant's sequence.</summary>
    public Task<string> NextDealNumberAsync(CancellationToken cancellationToken);

    /// <summary>Adds a new deal.</summary>
    public void Add(Deal deal);
}

/// <summary>
/// The calculation snapshot ledger.
/// </summary>
/// <remarks>
/// Append-only. There is deliberately no <c>Update</c> and no <c>Delete</c> on
/// this interface, because the table blocks both by trigger and the application
/// role is not granted either. An interface that offered them would be lying
/// about what the system can do.
/// </remarks>
public interface ICalculationStore
{
    /// <summary>The next version number for a deal, starting at 1.</summary>
    public Task<int> NextVersionAsync(Guid dealId, CancellationToken cancellationToken);

    /// <summary>Writes a snapshot.</summary>
    public void Add(DealCalculationRecord record);

    /// <summary>Reads a snapshot back.</summary>
    public Task<DealCalculationRecord?> GetAsync(Guid calculationId, CancellationToken cancellationToken);

    /// <summary>
    /// The authoritative snapshot for a deal, or null if it has never been priced.
    /// </summary>
    /// <remarks>
    /// <b>The highest version wins, and there is no "mark the old one superseded"
    /// step.</b> <c>sales.deal_calculation</c> carries a <c>superseded_at</c>
    /// column, but the immutability trigger raises on <em>any</em> update and the
    /// application role's UPDATE grant is revoked — so nothing, including the
    /// table owner, can ever set it. The column is vestigial; the V0004 comment
    /// describing a prior row being "marked superseded" describes something the
    /// schema forbids.
    /// <para>
    /// Supersession is therefore expressed the only way an append-only table can
    /// express it: a newer version exists, and <c>sales.deal.current_calculation_id</c>
    /// points at it. That is strictly better evidence than a mutable flag, because
    /// it cannot be set to something untrue.
    /// </para>
    /// </remarks>
    public Task<DealCalculationRecord?> GetCurrentAsync(Guid dealId, CancellationToken cancellationToken);
}

/// <summary>Reads tax rule sets.</summary>
public interface ITaxRuleSetStore
{
    /// <summary>
    /// The rule set governing a jurisdiction on a date.
    /// </summary>
    /// <remarks>
    /// <b>Returns unapproved rule sets too.</b> That is deliberate: filtering them
    /// out here would make the engine's refusal read as "no rules exist for this
    /// jurisdiction", when the truth is "rules exist and nobody has approved
    /// them". Those need different things from a human, so they must be
    /// distinguishable.
    /// </remarks>
    public Task<TaxRuleSetRow?> FindAsync(
        Guid jurisdictionId,
        DateOnly onDate,
        CancellationToken cancellationToken);
}

/// <summary>Projects deals straight to DTOs.</summary>
public interface IDealReadStore
{
    /// <summary>A deal with its current calculation, or null.</summary>
    public Task<DealDto?> GetAsync(Guid dealId, bool includeGross, CancellationToken cancellationToken);

    /// <summary>The deal list.</summary>
    public Task<PagedResult<DealSummaryDto>> ListAsync(
        DealListFilter filter,
        CancellationToken cancellationToken);
}

/// <summary>Filters for the deal list.</summary>
/// <remarks>
/// A fixed set of named fields rather than a query language, for the same reason
/// as the inventory grid: a filter DSL is both an injection surface and a
/// reliable way to generate queries no index can serve.
/// </remarks>
public sealed record DealListFilter
{
    /// <summary>Largest page a caller may ask for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Page size when none is given.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Restrict to these statuses.</summary>
    public IReadOnlyList<string>? Statuses { get; init; }

    /// <summary>Restrict to one customer.</summary>
    public Guid? CustomerId { get; init; }

    /// <summary>Restrict to one vehicle.</summary>
    public Guid? VehicleId { get; init; }

    /// <summary>Clamps rather than rejects an oversized page.</summary>
    public DealListFilter Normalized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize is < 1 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize),
    };
}

/// <summary>Commits the unit of work, outbox included.</summary>
public interface IUnitOfWork
{
    /// <summary>Saves everything tracked, in one transaction.</summary>
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
