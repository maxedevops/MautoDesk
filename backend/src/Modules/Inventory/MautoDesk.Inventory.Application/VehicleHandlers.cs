using System.Globalization;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Application;

/// <summary>Creates a vehicle from the minimum a lot walk supplies.</summary>
public sealed record CreateVehicleCommand
{
    public string? StockNumber { get; init; }

    public string? Vin { get; init; }

    public bool DecodeVin { get; init; } = true;

    public int? ModelYear { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Trim { get; init; }

    public int? Mileage { get; init; }

    public string? ExteriorColor { get; init; }

    public string? InteriorColor { get; init; }

    public string? ListPrice { get; init; }

    public string? Location { get; init; }

    public string? Notes { get; init; }
}

public sealed record ChangeVehicleStatusCommand(Guid VehicleId, string Status);

public sealed record SetVehiclePriceCommand(Guid VehicleId, string Price, string? Reason);

public sealed record PublishVehicleCommand(Guid VehicleId);

/// <summary>
/// Vehicle write operations.
/// </summary>
/// <remarks>
/// <b>Authorization is enforced here, in the application layer — not on the
/// endpoint.</b> An HTTP request is one caller; a background job, an import, and
/// an internal service call are others, and all of them must be gated. A check
/// that lives only in a controller protects exactly one of those four paths
/// (docs/02-architecture.md §5).
/// </remarks>
public sealed class VehicleCommandHandler
{
    private readonly IVehicleRepository _repository;
    private readonly IVehicleReadStore _readStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IVinDecoder _vinDecoder;
    private readonly IStockNumberGenerator _stockNumbers;
    private readonly IAuditLog _audit;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    public VehicleCommandHandler(
        IVehicleRepository repository,
        IVehicleReadStore readStore,
        IUnitOfWork unitOfWork,
        IVinDecoder vinDecoder,
        IStockNumberGenerator stockNumbers,
        IAuditLog audit,
        ITenantContext tenant,
        IClock clock)
    {
        _repository = repository;
        _readStore = readStore;
        _unitOfWork = unitOfWork;
        _vinDecoder = vinDecoder;
        _stockNumbers = stockNumbers;
        _audit = audit;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<Result<VehicleDto>> CreateAsync(
        CreateVehicleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.VehicleWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to add vehicles.");
        }

        var tenantId = _tenant.RequireTenantId();

        // A stock number is required by the domain but not by the user: if they
        // did not supply one, take the next in their sequence. Demanding it would
        // add a field to the fastest path in the product for no benefit.
        var stockNumberValue = string.IsNullOrWhiteSpace(command.StockNumber)
            ? await _stockNumbers.NextAsync(cancellationToken).ConfigureAwait(false)
            : command.StockNumber;

        var stockNumber = StockNumber.Create(stockNumberValue);
        if (stockNumber.IsFailure)
        {
            return stockNumber.Error!;
        }

        Vin? vin = null;
        if (!string.IsNullOrWhiteSpace(command.Vin))
        {
            var parsed = Vin.Create(command.Vin);
            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            vin = parsed.Value;
        }

        if (await _repository.StockNumberExistsAsync(stockNumber.Value.Value, null, cancellationToken)
                .ConfigureAwait(false))
        {
            return Error.Conflict(
                "vehicle.stock_number.duplicate",
                $"Stock number {stockNumber.Value.Value} is already in use.");
        }

        if (vin is not null &&
            await _repository.VinExistsAsync(vin.Value.Value, null, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(
                "vehicle.vin.duplicate",
                $"VIN {vin.Value.Value} is already in inventory.");
        }

        var creation = Vehicle.Create(tenantId, stockNumber.Value, vin, _clock.Today);
        if (creation.IsFailure)
        {
            return creation.Error!;
        }

        var vehicle = creation.Value;

        // Dealer-supplied values are applied BEFORE the decode, because
        // ApplyDecode only fills gaps. Someone reading the window sticker beats
        // a free government database every time.
        vehicle.SetIdentity(
            command.ModelYear,
            command.Make,
            command.Model,
            command.Trim,
            command.ExteriorColor,
            command.InteriorColor);

        if (command.Mileage is { } miles)
        {
            var mileage = Mileage.Create(miles);
            if (mileage.IsFailure)
            {
                return mileage.Error!;
            }

            vehicle.SetMileage(mileage.Value);
        }

        if (!string.IsNullOrWhiteSpace(command.ListPrice))
        {
            var price = Money.TryParse(command.ListPrice);
            if (price.IsFailure)
            {
                return price.Error!;
            }

            var applied = vehicle.SetListPrice(price.Value);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }
        }

        vehicle.SetLocation(command.Location);
        vehicle.SetNotes(command.Notes);

        if (vin is not null && command.DecodeVin)
        {
            // A decoder outage must never block booking a vehicle that is
            // physically on the lot. A failed decode is simply skipped; a
            // background retry fills the gaps later.
            var decode = await _vinDecoder.DecodeAsync(vin.Value, false, cancellationToken)
                .ConfigureAwait(false);

            if (decode.IsSuccess)
            {
                var d = decode.Value;
                vehicle.ApplyDecode(new VehicleDecodeResult(
                    d.ModelYear, d.Make, d.Model, d.Trim, d.BodyStyle,
                    d.DriveType, d.Engine, d.FuelType, d.Transmission));
            }
        }

        _repository.Add(vehicle);

        // Recorded before the save, so the ledger entry is part of the same
        // transaction as the vehicle. There is no window where one exists
        // without the other.
        _audit.Record(new AuditEntry
        {
            Action = "inventory.vehicle.created",
            EntitySchema = "inventory",
            EntityType = "vehicle",
            EntityId = vehicle.Id,
            After = new { vehicle.StockNumber, Vin = vehicle.Vin, Status = vehicle.Status.ToString() },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var created = await _readStore.GetAsync(vehicle.Id, cancellationToken).ConfigureAwait(false);
        return created is null
            ? Error.NotFound("vehicle.not_found", "The vehicle could not be read back after saving.")
            : Result<VehicleDto>.Success(created);
    }

    public async Task<Result<VehicleDto>> ChangeStatusAsync(
        ChangeVehicleStatusCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.VehicleWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to change vehicle status.");
        }

        // Existence is resolved BEFORE the payload is validated. With the
        // opposite ordering a caller could tell a resource they cannot see from
        // one that does not exist by sending a deliberately invalid body: a
        // foreign id would answer 422 (their input) where a missing id answers
        // 404. It is not exploitable on this particular handler — the message
        // says nothing about the vehicle — but the ordering is the thing that
        // makes it safe, and relying on "this message happens to be harmless"
        // does not survive the next handler someone writes.
        var vehicle = await _repository.GetByIdAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return NotFound(command.VehicleId);
        }

        if (!Enum.TryParse<VehicleStatus>(command.Status, ignoreCase: true, out var target))
        {
            return Error.Validation("vehicle.status.unknown", $"'{command.Status}' is not a vehicle status.", "status");
        }

        var previous = vehicle.Status;
        var changed = vehicle.ChangeStatus(target, _clock.Today);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        _audit.Record(new AuditEntry
        {
            Action = "inventory.vehicle.status_changed",
            EntitySchema = "inventory",
            EntityType = "vehicle",
            EntityId = vehicle.Id,
            Before = new { Status = previous.ToString() },
            After = new { Status = vehicle.Status.ToString() },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBackAsync(vehicle.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<VehicleDto>> SetPriceAsync(
        SetVehiclePriceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.PriceWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to change prices.");
        }

        // Existence first, for the same reason as ChangeStatusAsync above.
        var vehicle = await _repository.GetByIdAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return NotFound(command.VehicleId);
        }

        var price = Money.TryParse(command.Price);
        if (price.IsFailure)
        {
            return price.Error!;
        }

        var previousPrice = vehicle.ListPrice;
        var applied = vehicle.SetListPrice(price.Value);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        // "Who changed this price?" is the question this ledger was built to
        // answer, so the two numbers are recorded as strings — a JSON number
        // would round a price through a double on its way into the record.
        _audit.Record(new AuditEntry
        {
            Action = "inventory.vehicle.price_changed",
            EntitySchema = "inventory",
            EntityType = "vehicle",
            EntityId = vehicle.Id,
            Before = new { ListPrice = previousPrice?.ToString(CultureInfo.InvariantCulture) },
            After = new { ListPrice = vehicle.ListPrice?.ToString(CultureInfo.InvariantCulture) },
            Metadata = command.Reason is null
                ? null
                : new Dictionary<string, object?> { ["reason"] = command.Reason },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBackAsync(vehicle.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<VehicleDto>> PublishAsync(
        PublishVehicleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.Publish))
        {
            return Error.Forbidden("forbidden", "You do not have permission to publish vehicles.");
        }

        var vehicle = await _repository.GetByIdAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return NotFound(command.VehicleId);
        }

        var photoCount = await _repository.CountPhotosAsync(vehicle.Id, cancellationToken).ConfigureAwait(false);

        var published = vehicle.Publish(photoCount);
        if (published.IsFailure)
        {
            return published.Error!;
        }

        _audit.Record(new AuditEntry
        {
            Action = "inventory.vehicle.published",
            EntitySchema = "inventory",
            EntityType = "vehicle",
            EntityId = vehicle.Id,
            After = new { vehicle.IsPublished, ListPrice = vehicle.ListPrice?.ToString(CultureInfo.InvariantCulture), PhotoCount = photoCount },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBackAsync(vehicle.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(InventoryPermissions.VehicleDelete))
        {
            return Error.Forbidden("forbidden", "You do not have permission to delete vehicles.");
        }

        var vehicle = await _repository.GetByIdAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return Error.NotFound("vehicle.not_found", "That vehicle does not exist.");
        }

        var deleted = vehicle.Delete(_clock.UtcNow, _tenant.UserId);
        if (deleted.IsFailure)
        {
            return deleted.Error!;
        }

        // A soft delete is still the removal of a record from everyone's view,
        // and it is the sort of thing someone asks about six months later.
        _audit.Record(new AuditEntry
        {
            Action = "inventory.vehicle.deleted",
            EntitySchema = "inventory",
            EntityType = "vehicle",
            EntityId = vehicle.Id,
            Before = new { vehicle.StockNumber, Status = vehicle.Status.ToString() },
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The response for a vehicle that does not exist <em>or</em> belongs to
    /// another tenant.
    /// </summary>
    /// <remarks>
    /// These two cases are indistinguishable on purpose. Row-level security
    /// makes a cross-tenant read return zero rows, and reporting that as 403
    /// rather than 404 would confirm the record exists — an information leak
    /// that turns an id into an oracle.
    /// </remarks>
    private static Error NotFound(Guid vehicleId) =>
        Error.NotFound("vehicle.not_found", $"No vehicle with id {vehicleId} was found.");

    private async Task<Result<VehicleDto>> ReadBackAsync(Guid id, CancellationToken cancellationToken)
    {
        var dto = await _readStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return dto is null ? NotFound(id) : Result<VehicleDto>.Success(dto);
    }
}

/// <summary>Vehicle read operations.</summary>
public sealed class VehicleQueryHandler
{
    private readonly IVehicleReadStore _readStore;
    private readonly IVinDecoder _vinDecoder;
    private readonly ITenantContext _tenant;

    public VehicleQueryHandler(
        IVehicleReadStore readStore,
        IVinDecoder vinDecoder,
        ITenantContext tenant)
    {
        _readStore = readStore;
        _vinDecoder = vinDecoder;
        _tenant = tenant;
    }

    public async Task<Result<PagedResult<VehicleSummaryDto>>> ListAsync(
        VehicleListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!_tenant.HasPermission(InventoryPermissions.VehicleRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view inventory.");
        }

        var page = await _readStore.ListAsync(filter.Normalized(), cancellationToken).ConfigureAwait(false);
        return Result<PagedResult<VehicleSummaryDto>>.Success(page);
    }

    public async Task<Result<VehicleDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(InventoryPermissions.VehicleRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view inventory.");
        }

        var dto = await _readStore.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return dto is null
            ? Error.NotFound("vehicle.not_found", $"No vehicle with id {id} was found.")
            : Result<VehicleDto>.Success(dto);
    }

    public async Task<Result<VinDecodeDto>> DecodeVinAsync(
        string vin,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(InventoryPermissions.VehicleRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to decode VINs.");
        }

        var parsed = Vin.Create(vin);
        return parsed.IsFailure
            ? parsed.Error!
            : await _vinDecoder.DecodeAsync(parsed.Value, refresh, cancellationToken).ConfigureAwait(false);
    }
}
