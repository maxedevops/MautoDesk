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

    // The status moves this vehicle may make right now, so a client can offer
    // only what will succeed rather than duplicating the transition table.
    IReadOnlyList<string> AllowedTransitions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>How close a vehicle is to being publishable.</summary>
public sealed record PublishReadinessDto(int Satisfied, int Total, IReadOnlyList<string> Missing);

/// <summary>
/// Permission to upload one file, and where to put it.
/// </summary>
/// <remarks>
/// The URL points at the quarantine bucket and expires. Uploading to it is not
/// the end of the story — the file is nothing until it is confirmed and passes
/// verification.
/// </remarks>
public sealed record PhotoUploadIntentDto(Guid PhotoId, string UploadUrl, int ExpiresIn);

/// <summary>
/// A photo, or an attempt at one.
/// </summary>
/// <remarks>
/// <c>Url</c> is null unless <c>Status</c> is <c>ready</c>: a pending or
/// rejected row is still reported so the screen can show what happened, but
/// there is deliberately nothing to fetch.
/// </remarks>
public sealed record VehiclePhotoDto(
    Guid Id,
    string? Url,
    string? ThumbnailUrl,
    int? Width,
    int? Height,
    bool IsPrimary,
    int SortOrder,
    string? Caption,
    string Status,
    string? RejectionReason);

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
