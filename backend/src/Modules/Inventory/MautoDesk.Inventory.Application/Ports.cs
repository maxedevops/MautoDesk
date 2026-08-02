using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Application;

/// <summary>Persistence for the <see cref="Vehicle"/> aggregate.</summary>
/// <remarks>
/// <para>
/// Every method is implicitly tenant-scoped. Not by passing a tenant id around —
/// that is a parameter someone eventually forgets — but because the connection
/// itself carries <c>app.tenant_id</c> and PostgreSQL row-level security filters
/// underneath. A query for another tenant's vehicle returns nothing, which
/// surfaces as a 404 (ADR-0002).
/// </para>
/// <para>
/// Reads for grids live on <see cref="IVehicleReadStore"/>, not here. Loading 500
/// aggregates to render a table is the N+1 problem wearing a nicer suit.
/// </para>
/// </remarks>
public interface IVehicleRepository
{
    public Task<Vehicle?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    public Task<bool> StockNumberExistsAsync(string stockNumber, Guid? excludingId, CancellationToken cancellationToken);

    public Task<bool> VinExistsAsync(string vin, Guid? excludingId, CancellationToken cancellationToken);

    public Task<int> CountPhotosAsync(Guid vehicleId, CancellationToken cancellationToken);

    public void Add(Vehicle vehicle);
}

/// <summary>
/// The read side: projections straight to DTOs.
/// </summary>
/// <remarks>
/// The CQRS split from docs/02-architecture.md §3, applied only where it earns
/// its keep. Same database, same connection, same RLS — a different path that
/// does not materialize domain objects.
/// </remarks>
public interface IVehicleReadStore
{
    public Task<PagedResult<VehicleSummaryDto>> ListAsync(
        VehicleListFilter filter,
        CancellationToken cancellationToken);

    public Task<VehicleDto?> GetAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Filters for the inventory grid.</summary>
/// <remarks>
/// Every filter here maps to an index built in Phase 3 §6. This type is
/// deliberately a fixed set of named fields rather than a generic query
/// language: a filter DSL is both an injection surface and a reliable way to
/// generate queries no index can serve (docs/04-api-contracts.md §5).
/// </remarks>
public sealed record VehicleListFilter
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    public IReadOnlyList<VehicleStatus>? Statuses { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Search { get; init; }

    public bool? IsPublished { get; init; }

    public int? AgeDaysMin { get; init; }

    public string? Sort { get; init; }

    /// <summary>Clamps rather than rejects an oversized page.</summary>
    /// <remarks>
    /// A client asking for 5,000 rows gets 100, not a 422. Rejecting would be
    /// pedantic; returning 5,000 would let one request degrade the tenant's
    /// whole experience.
    /// </remarks>
    public VehicleListFilter Normalized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize is < 1 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize),
    };
}

/// <summary>Commits the unit of work.</summary>
/// <remarks>
/// The implementation writes domain events to the outbox in the same
/// transaction as the state change (ADR-0006), so a vehicle that saves always
/// eventually publishes.
/// </remarks>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Decodes a VIN into vehicle identity fields.</summary>
/// <remarks>
/// <para>
/// Abstracted from the first commit because the launch provider is deliberately
/// temporary. NHTSA vPIC is free and needs no key, but its trim and factory
/// option data is incomplete for many vehicles — which matters the moment
/// pricing depends on trim. Swapping in a paid decoder must be a registration
/// change, not a refactor.
/// </para>
/// <para>
/// Implementations must not throw on an upstream failure; they return an
/// <see cref="ErrorKind.Unavailable"/> result so the caller can continue with
/// manual entry rather than being blocked by someone else's outage.
/// </para>
/// </remarks>
public interface IVinDecoder
{
    public Task<Result<VinDecodeDto>> DecodeAsync(
        Vin vin,
        bool bypassCache,
        CancellationToken cancellationToken);
}

/// <summary>Generates the next stock number in a tenant's sequence.</summary>
public interface IStockNumberGenerator
{
    public Task<string> NextAsync(CancellationToken cancellationToken);
}
