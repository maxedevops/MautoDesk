namespace MautoDesk.Inventory.Contracts;

/// <summary>
/// The Inventory module's public surface.
/// </summary>
/// <remarks>
/// This is the <b>only</b> project in the Inventory module that another module
/// may reference. An architecture test fails the build if anything reaches into
/// <c>Inventory.Domain</c>, <c>.Application</c>, or <c>.Infrastructure</c> from
/// outside — which is what keeps the modular monolith of ADR-0001 from quietly
/// becoming a ball of mud, and what makes extracting this module into its own
/// service later a mechanical job rather than an archaeology project.
/// </remarks>
public static class InventoryPermissions
{
    public const string VehicleRead = "inventory.vehicle.read";
    public const string VehicleWrite = "inventory.vehicle.write";
    public const string VehicleDelete = "inventory.vehicle.delete";
    public const string CostRead = "inventory.cost.read";
    public const string CostWrite = "inventory.cost.write";
    public const string PriceWrite = "inventory.price.write";
    public const string PhotoWrite = "inventory.photo.write";
    public const string Publish = "inventory.publish";
}

/// <summary>A vehicle as other modules and the API see it.</summary>
/// <remarks>
/// Money is a decimal <b>string</b>, matching the wire contract. Never a
/// <c>double</c>, and never a JSON number — a price that round-trips through
/// IEEE-754 in a browser is a price that can be wrong by a cent on a contract.
/// </remarks>
public sealed record VehicleSummaryDto(
    Guid Id,
    string StockNumber,
    string? Vin,
    int? ModelYear,
    string? Make,
    string? Model,
    string? Trim,
    int? Mileage,
    string? ExteriorColor,
    string Status,
    string? ListPrice,
    int? DaysInInventory,
    int? DaysToSale,
    bool IsPublished,
    int PhotoCount);

public sealed record VehicleDto(
    Guid Id,
    string StockNumber,
    string? Vin,
    int? ModelYear,
    string? Make,
    string? Model,
    string? Trim,
    string? BodyStyle,
    string? DriveType,
    string? Engine,
    string? FuelType,
    string? Transmission,
    string? ExteriorColor,
    string? InteriorColor,
    int? Mileage,
    string Status,
    string? ListPrice,
    string? Description,
    string? AiDescriptionDraft,
    DateOnly? AcquiredAt,
    DateOnly? AvailableAt,
    DateOnly? SoldAt,
    bool IsPublished,
    string? Location,
    string? Notes,
    int? DaysInInventory,
    PublishReadinessDto Readiness,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>How close a vehicle is to being publishable.</summary>
public sealed record PublishReadinessDto(int Satisfied, int Total, IReadOnlyList<string> Missing);

/// <summary>A decoded VIN.</summary>
public sealed record VinDecodeDto(
    string Vin,
    string Provider,
    bool FromCache,
    bool CheckDigitValid,
    int? ModelYear,
    string? Make,
    string? Model,
    string? Trim,
    string? BodyStyle,
    string? DriveType,
    string? Engine,
    string? FuelType,
    string? Transmission,
    string? Manufacturer,
    string? ErrorText);

/// <summary>One page of results.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
