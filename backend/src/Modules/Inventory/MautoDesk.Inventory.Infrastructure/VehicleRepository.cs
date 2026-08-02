using System.Globalization;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace MautoDesk.Inventory.Infrastructure;

/// <summary>EF-backed persistence for the vehicle aggregate.</summary>
/// <remarks>
/// Note the absence of any <c>WHERE tenant_id = ...</c> clause. That is not an
/// oversight: the connection carries <c>app.tenant_id</c> and PostgreSQL
/// row-level security filters underneath every one of these queries. Adding an
/// application-level tenant predicate here would be belt-and-braces, but it
/// would also invite the belief that the predicate is what protects us — and the
/// day someone writes a query without it, that belief becomes a breach. The
/// database is the authority (ADR-0002).
/// </remarks>
public sealed class VehicleRepository : IVehicleRepository
{
    private readonly MautoDeskDbContext _db;

    public VehicleRepository(MautoDeskDbContext db) => _db = db;

    public Task<Vehicle?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<Vehicle>().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public Task<bool> StockNumberExistsAsync(
        string stockNumber,
        Guid? excludingId,
        CancellationToken cancellationToken) =>
        _db.Set<Vehicle>()
            .AnyAsync(
                v => v.StockNumber == stockNumber && (excludingId == null || v.Id != excludingId),
                cancellationToken);

    public Task<bool> VinExistsAsync(string vin, Guid? excludingId, CancellationToken cancellationToken) =>
        _db.Set<Vehicle>()
            .AnyAsync(
                v => v.Vin == vin && (excludingId == null || v.Id != excludingId),
                cancellationToken);

    public async Task<int> CountPhotosAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        // Photos are not yet an aggregate in this slice, so this reads the table
        // directly. It is a scalar count against an indexed column, and it is
        // still subject to RLS on the same connection.
        // EF wraps a SqlQueryRaw scalar in `select s."Value" from (...) as s`, so the
        // projected column must literally be named "Value".
        var sql = """select count(*)::int as "Value" from inventory.vehicle_photo where vehicle_id = {0} and deleted_at is null and processing_status = 'ready'""";

        return await _db.Database
            .SqlQueryRaw<int>(sql, vehicleId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Add(Vehicle vehicle) => _db.Set<Vehicle>().Add(vehicle);
}

/// <summary>Commits the unit of work, outbox included.</summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly MautoDeskDbContext _db;

    public UnitOfWork(MautoDeskDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}

/// <summary>Projects vehicles straight to DTOs for grids and detail views.</summary>
public sealed class VehicleReadStore : IVehicleReadStore
{
    private readonly MautoDeskDbContext _db;
    private readonly IClock _clock;

    public VehicleReadStore(MautoDeskDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PagedResult<VehicleSummaryDto>> ListAsync(
        VehicleListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.Set<Vehicle>().AsNoTracking();

        if (filter.Statuses is { Count: > 0 })
        {
            query = query.Where(v => filter.Statuses.Contains(v.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.Make))
        {
            query = query.Where(v => v.Make == filter.Make);
        }

        if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            query = query.Where(v => v.Model == filter.Model);
        }

        if (filter.IsPublished is { } published)
        {
            query = query.Where(v => v.IsPublished == published);
        }

        if (filter.AgeDaysMin is { } minDays)
        {
            var cutoff = _clock.Today.AddDays(-minDays);
            query = query.Where(v => v.AcquiredAt != null && v.AcquiredAt <= cutoff);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // A lot-floor search: stock number, VIN (usually the last six), make
            // or model. Backed by the trigram indexes from Phase 3 §6, which is
            // why partial VIN matching is a contains rather than a prefix — a
            // dealer types the middle of the number, not the start.
            var term = filter.Search.Trim();
            query = query.Where(v =>
                EF.Functions.ILike(v.StockNumber, $"%{term}%") ||
                (v.Vin != null && EF.Functions.ILike(v.Vin, $"%{term}%")) ||
                (v.Make != null && EF.Functions.ILike(v.Make, $"%{term}%")) ||
                (v.Model != null && EF.Functions.ILike(v.Model, $"%{term}%")));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        query = ApplySort(query, filter.Sort);

        var rows = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(v => new
            {
                v.Id,
                v.StockNumber,
                v.Vin,
                v.ModelYear,
                v.Make,
                v.Model,
                v.Trim,
                v.Mileage,
                v.ExteriorColor,
                v.Status,
                v.ListPrice,
                v.AcquiredAt,
                v.SoldAt,
                v.IsPublished,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Photo counts for the page in ONE query, keyed by vehicle. A correlated
        // subquery per row would be the N+1 the performance budget forbids, and
        // it is the shape this grid would most easily acquire it in.
        var ids = rows.Select(r => r.Id).ToArray();
        var photoCounts = await GetPhotoCountsAsync(ids, cancellationToken).ConfigureAwait(false);

        var today = _clock.Today;

        var items = rows.Select(r => new VehicleSummaryDto(
            r.Id,
            r.StockNumber,
            r.Vin,
            r.ModelYear,
            r.Make,
            r.Model,
            r.Trim,
            r.Mileage,
            r.ExteriorColor,
            ToWire(r.Status),
            FormatMoney(r.ListPrice),
            DaysInInventory(r.AcquiredAt, r.SoldAt, today),
            DaysToSale(r.AcquiredAt, r.SoldAt),
            r.IsPublished,
            photoCounts.GetValueOrDefault(r.Id))).ToList();

        return new PagedResult<VehicleSummaryDto>(items, filter.Page, filter.PageSize, totalCount);
    }

    /// <summary>Photo counts for a set of vehicles, in one round trip.</summary>
    private async Task<Dictionary<Guid, int>> GetPhotoCountsAsync(
        Guid[] vehicleIds,
        CancellationToken cancellationToken)
    {
        if (vehicleIds.Length == 0)
        {
            return [];
        }

        var rows = await _db.Database
            .SqlQueryRaw<PhotoCountRow>(
                """
                select vehicle_id as "VehicleId", count(*)::int as "Count"
                  from inventory.vehicle_photo
                 where vehicle_id = any({0}) and deleted_at is null and processing_status = 'ready'
                 group by vehicle_id
                """,
                vehicleIds)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(row => row.VehicleId, row => row.Count);
    }

    private sealed record PhotoCountRow(Guid VehicleId, int Count);

    public async Task<VehicleDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicle = await _db.Set<Vehicle>()
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (vehicle is null)
        {
            return null;
        }

        var photoCount = await _db.Database
            .SqlQueryRaw<int>(
                """select count(*)::int as "Value" from inventory.vehicle_photo where vehicle_id = {0} and deleted_at is null and processing_status = 'ready'""",
                id)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        var readiness = vehicle.GetPublishReadiness(photoCount);
        var today = _clock.Today;

        return new VehicleDto(
            vehicle.Id,
            vehicle.StockNumber,
            vehicle.Vin,
            vehicle.ModelYear,
            vehicle.Make,
            vehicle.Model,
            vehicle.Trim,
            vehicle.BodyStyle,
            vehicle.DriveType,
            vehicle.Engine,
            vehicle.FuelType,
            vehicle.Transmission,
            vehicle.ExteriorColor,
            vehicle.InteriorColor,
            vehicle.Mileage,
            ToWire(vehicle.Status),
            FormatMoney(vehicle.ListPrice),
            vehicle.Description,
            vehicle.AiDescriptionDraft,
            vehicle.AcquiredAt,
            vehicle.AvailableAt,
            vehicle.SoldAt,
            vehicle.IsPublished,
            vehicle.Location,
            vehicle.Notes,
            DaysInInventory(vehicle.AcquiredAt, vehicle.SoldAt, today),
            new PublishReadinessDto(readiness.Satisfied, readiness.Total, readiness.Missing),
            [.. vehicle.AllowedTransitions.Select(ToWire)],
            vehicle.CreatedAt,
            vehicle.UpdatedAt);
    }

    /// <summary>
    /// Applies a whitelisted sort.
    /// </summary>
    /// <remarks>
    /// The sort parameter never reaches SQL as a string. Only these fields are
    /// sortable, and an unrecognized value falls back to the default rather than
    /// erroring — a client sending a stale field name should get a sensible page,
    /// not a 400.
    /// </remarks>
    private static IQueryable<Vehicle> ApplySort(IQueryable<Vehicle> query, string? sort)
    {
        var descending = sort?.StartsWith('-') == true;
        var field = sort?.TrimStart('-').Trim().ToUpperInvariant();

        return field switch
        {
            "STOCKNUMBER" => descending
                ? query.OrderByDescending(v => v.StockNumber)
                : query.OrderBy(v => v.StockNumber),
            "LISTPRICE" => descending
                ? query.OrderByDescending(v => v.ListPrice)
                : query.OrderBy(v => v.ListPrice),
            "MILEAGE" => descending
                ? query.OrderByDescending(v => v.Mileage)
                : query.OrderBy(v => v.Mileage),
            "MODELYEAR" => descending
                ? query.OrderByDescending(v => v.ModelYear)
                : query.OrderBy(v => v.ModelYear),
            "ACQUIREDAT" => descending
                ? query.OrderByDescending(v => v.AcquiredAt)
                : query.OrderBy(v => v.AcquiredAt),

            // Oldest first. Aging inventory is the thing a dealer most needs to
            // see, so it is the default rather than something to opt into.
            _ => query.OrderBy(v => v.AcquiredAt).ThenBy(v => v.Id),
        };
    }

    private static int? DaysInInventory(DateOnly? acquired, DateOnly? sold, DateOnly today) =>
        acquired is null || sold is not null ? null : today.DayNumber - acquired.Value.DayNumber;

    private static int? DaysToSale(DateOnly? acquired, DateOnly? sold) =>
        acquired is null || sold is null ? null : sold.Value.DayNumber - acquired.Value.DayNumber;

    private static string? FormatMoney(decimal? amount) =>
        amount?.ToString("0.00", CultureInfo.InvariantCulture);

    private static string ToWire(VehicleStatus status) => status switch
    {
        VehicleStatus.InRecon => "in_recon",
        VehicleStatus.OnHold => "on_hold",
        VehicleStatus.PendingSale => "pending_sale",
        _ => status.ToString().ToUpperInvariant() switch
        {
            "ACQUIRED" => "acquired",
            "AVAILABLE" => "available",
            "SOLD" => "sold",
            "DELIVERED" => "delivered",
            "WHOLESALED" => "wholesaled",
            "ARCHIVED" => "archived",
            _ => status.ToString(),
        },
    };
}

/// <summary>Generates the next stock number in a tenant's sequence.</summary>
/// <remarks>
/// Deliberately simple: count the tenant's vehicles and add one, prefixed. Real
/// dealers have their own conventions and will usually type their own, so this
/// is a convenience for the fast path, not a system of record. It is also why
/// a collision is handled by the caller's duplicate check rather than by a
/// database sequence — a sequence would impose a format on every tenant.
/// </remarks>
public sealed class StockNumberGenerator : IStockNumberGenerator
{
    private readonly MautoDeskDbContext _db;

    public StockNumberGenerator(MautoDeskDbContext db) => _db = db;

    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        var count = await _db.Set<Vehicle>().IgnoreQueryFilters().CountAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Create(CultureInfo.InvariantCulture, $"A-{count + 1001}");
    }
}
