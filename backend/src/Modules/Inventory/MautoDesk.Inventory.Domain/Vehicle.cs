using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Domain;

/// <summary>Where a vehicle is in its life on the lot.</summary>
/// <remarks>Mirrors the check constraint on <c>inventory.vehicle.status</c>.</remarks>
public enum VehicleStatus
{
    Acquired,
    InRecon,
    Available,
    OnHold,
    PendingSale,
    Sold,
    Delivered,
    Wholesaled,
    Archived,
}

/// <summary>A vehicle in a dealer's inventory. The aggregate root.</summary>
/// <remarks>
/// <para>
/// <b>Almost every field is optional.</b> That is the single most important
/// design decision in this class. A salesperson standing on a lot with a
/// customer waiting has a VIN and a stock number; they do not have the trim
/// level, the reconditioning cost, or a description. A DMS that refuses to save
/// until eleven fields are filled loses to a spiral notebook, so completeness is
/// reported (<see cref="PublishReadiness"/>) rather than enforced.
/// </para>
/// <para>
/// Costs deliberately do not live here. They are a separate aggregate so that
/// "the salesperson's view of a vehicle" is a query that never joins them,
/// rather than a projection someone must remember to strip on every endpoint.
/// See docs/03-database-design.md §5.1.
/// </para>
/// </remarks>
public sealed class Vehicle : AggregateRoot
{
    private Vehicle(Guid id, Guid tenantId, StockNumber stockNumber)
        : base(id, tenantId)
    {
        StockNumber = stockNumber.Value;
    }

    /// <summary>Required by EF Core materialization.</summary>
    private Vehicle()
    {
    }

    public string StockNumber { get; private set; } = string.Empty;

    public string? Vin { get; private set; }

    public int? ModelYear { get; private set; }

    public string? Make { get; private set; }

    public string? Model { get; private set; }

    public string? Trim { get; private set; }

    public string? BodyStyle { get; private set; }

    public string? DriveType { get; private set; }

    public string? Engine { get; private set; }

    public string? FuelType { get; private set; }

    public string? Transmission { get; private set; }

    public string? ExteriorColor { get; private set; }

    public string? InteriorColor { get; private set; }

    public int? Mileage { get; private set; }

    public VehicleStatus Status { get; private set; } = VehicleStatus.Acquired;

    public decimal? ListPrice { get; private set; }

    public string? Description { get; private set; }

    /// <summary>
    /// AI-generated copy awaiting human approval.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Description"/> on purpose. Publishing
    /// un-reviewed AI text about a vehicle's equipment is a consumer-protection
    /// risk — advertising a feature the car does not have — so the model's output
    /// cannot become the published description without someone holding
    /// <c>ai.approve</c> calling <see cref="ApproveAiDescription"/>. The
    /// architecture makes it impossible, not merely discouraged (ADR-0004).
    /// </remarks>
    public string? AiDescriptionDraft { get; private set; }

    public DateTimeOffset? AiDescriptionApprovedAt { get; private set; }

    public DateOnly? AcquiredAt { get; private set; }

    public DateOnly? AvailableAt { get; private set; }

    public DateOnly? SoldAt { get; private set; }

    public bool IsPublished { get; private set; }

    public string? Location { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Creates a vehicle from the minimum a lot walk can supply.</summary>
    public static Result<Vehicle> Create(
        Guid tenantId,
        StockNumber stockNumber,
        Vin? vin,
        DateOnly today)
    {
        var vehicle = new Vehicle(Guid.CreateVersion7(), tenantId, stockNumber)
        {
            Vin = vin?.Value,
            AcquiredAt = today,
            Status = VehicleStatus.Acquired,
        };

        vehicle.Raise(new VehicleCreated(vehicle.Id, tenantId, stockNumber.Value, vin?.Value));
        return vehicle;
    }

    /// <summary>
    /// Fills identity fields from a VIN decode without overwriting dealer input.
    /// </summary>
    /// <remarks>
    /// A decoder is a suggestion, not an authority. If a dealer has corrected the
    /// trim because they are looking at the window sticker, a later decode — or a
    /// provider changing its data three years from now — must not silently undo
    /// that. So this only fills fields that are currently empty.
    /// </remarks>
    public void ApplyDecode(VehicleDecodeResult decode)
    {
        ArgumentNullException.ThrowIfNull(decode);

        ModelYear ??= decode.ModelYear;
        Make ??= decode.Make;
        Model ??= decode.Model;
        Trim ??= decode.Trim;
        BodyStyle ??= decode.BodyStyle;
        DriveType ??= decode.DriveType;
        Engine ??= decode.Engine;
        FuelType ??= decode.FuelType;
        Transmission ??= decode.Transmission;
    }

    public Result SetVin(Vin vin)
    {
        if (Status is VehicleStatus.Sold or VehicleStatus.Delivered)
        {
            return Error.Conflict(
                "vehicle.vin.sold",
                "The VIN cannot be changed after a vehicle is sold — it identifies the unit on a " +
                "signed contract and a title application.");
        }

        Vin = vin.Value;
        return Result.Success();
    }

    public void SetMileage(Mileage mileage) => Mileage = mileage.Value;

    public void SetIdentity(
        int? modelYear,
        string? make,
        string? model,
        string? trim,
        string? exteriorColor,
        string? interiorColor)
    {
        ModelYear = modelYear;
        Make = make;
        Model = model;
        Trim = trim;
        ExteriorColor = exteriorColor;
        InteriorColor = interiorColor;
    }

    public Result SetListPrice(Money price)
    {
        if (price.IsNegative)
        {
            return Error.Validation("vehicle.price.negative", "A list price cannot be negative.", "listPrice");
        }

        var previous = ListPrice;
        ListPrice = price.Amount;

        if (previous != price.Amount)
        {
            Raise(new VehiclePriceChanged(Id, TenantId, previous, price.Amount));
        }

        return Result.Success();
    }

    public void SetDescription(string? description) => Description = description;

    public void SetLocation(string? location) => Location = location;

    public void SetNotes(string? notes) => Notes = notes;

    /// <summary>Records an AI draft. Does not publish it.</summary>
    public void ProposeAiDescription(string draft)
    {
        AiDescriptionDraft = draft;
        AiDescriptionApprovedAt = null;
    }

    /// <summary>Promotes the AI draft to the published description.</summary>
    /// <remarks>
    /// The caller must hold <c>ai.approve</c>; that is checked in the application
    /// layer. This method exists so there is exactly one code path by which model
    /// output can reach a consumer, and it is named.
    /// </remarks>
    public Result ApproveAiDescription(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(AiDescriptionDraft))
        {
            return Error.Conflict("vehicle.ai.no_draft", "There is no AI draft to approve.");
        }

        Description = AiDescriptionDraft;
        AiDescriptionApprovedAt = now;
        return Result.Success();
    }

    /// <summary>Moves the vehicle through its lifecycle, validating the transition.</summary>
    /// <summary>
    /// The statuses this vehicle may legally move to right now.
    /// </summary>
    /// <remarks>
    /// Published so a client can offer exactly the moves that will succeed,
    /// instead of reimplementing the transition table and drifting from it. The
    /// server still validates — this is a convenience for the UI, never the
    /// enforcement point.
    /// </remarks>
    public IReadOnlyList<VehicleStatus> AllowedTransitions =>
        [.. Enum.GetValues<VehicleStatus>().Where(target => IsTransitionAllowed(Status, target))];

    public Result ChangeStatus(VehicleStatus target, DateOnly today)
    {
        if (target == Status)
        {
            return Result.Success();
        }

        if (!IsTransitionAllowed(Status, target))
        {
            return Error.Conflict(
                "vehicle.status.invalid_transition",
                $"A vehicle cannot go from {Status} to {target}.");
        }

        var previous = Status;
        Status = target;

        // available_at is what makes "days to front line" measurable, and it is
        // stamped the first time a unit becomes sellable rather than every time,
        // so a vehicle that goes back into recon does not restart its clock.
        if (target == VehicleStatus.Available)
        {
            AvailableAt ??= today;
        }

        if (target is VehicleStatus.Sold or VehicleStatus.Wholesaled)
        {
            SoldAt = today;
            IsPublished = false;
        }

        Raise(new VehicleStatusChanged(Id, TenantId, previous.ToString(), target.ToString()));
        return Result.Success();
    }

    /// <summary>
    /// Publishes to the website and syndication channels.
    /// </summary>
    /// <param name="photoCount">Photos currently attached.</param>
    public Result Publish(int photoCount)
    {
        if (Status != VehicleStatus.Available)
        {
            return Error.Conflict(
                "vehicle.publish.not_available",
                $"Only an available vehicle can be published; this one is {Status}.");
        }

        // A listing with no photo and no price is one a shopper bounces off, and
        // it damages the dealer's placement on the marketplace. Refusing here is
        // kinder than publishing something that will not sell.
        if (photoCount == 0)
        {
            return Error.Validation(
                "vehicle.publish.no_photos",
                "Add at least one photo before publishing. Listings without photos are skipped by shoppers.");
        }

        if (ListPrice is null)
        {
            return Error.Validation(
                "vehicle.publish.no_price",
                "Set a price before publishing, or the listing will be filtered out of most searches.");
        }

        IsPublished = true;
        Raise(new VehiclePublished(Id, TenantId));
        return Result.Success();
    }

    public void Unpublish() => IsPublished = false;

    public Result Delete(DateTimeOffset now, Guid? userId)
    {
        if (Status is VehicleStatus.Sold or VehicleStatus.Delivered)
        {
            return Error.Conflict(
                "vehicle.delete.sold",
                "A sold vehicle cannot be deleted. It is referenced by a deal and by statutory " +
                "retention; archive it instead.");
        }

        DeletedAt = now;
        DeletedBy = userId;
        IsPublished = false;
        return Result.Success();
    }

    /// <summary>
    /// How close this vehicle is to being publishable.
    /// </summary>
    /// <remarks>
    /// Surfaced as a meter in the UI. This is the mechanism that lets the save
    /// path stay permissive without inventory quietly filling with unsellable
    /// records: the system asks rather than blocks.
    /// </remarks>
    public PublishReadiness GetPublishReadiness(int photoCount)
    {
        var checks = new List<(string Requirement, bool Satisfied)>
        {
            ("VIN", !string.IsNullOrWhiteSpace(Vin)),
            ("Year, make and model", ModelYear is not null && !string.IsNullOrWhiteSpace(Make)),
            ("Mileage", Mileage is not null),
            ("Price", ListPrice is not null),
            ("At least one photo", photoCount > 0),
            ("Description", !string.IsNullOrWhiteSpace(Description)),
        };

        return new PublishReadiness(
            checks.Count(check => check.Satisfied),
            checks.Count,
            checks.Where(check => !check.Satisfied).Select(check => check.Requirement).ToList());
    }

    /// <summary>
    /// The lifecycle state machine.
    /// </summary>
    /// <remarks>
    /// Explicitly permissive about going backwards — a sale falls through, a unit
    /// comes back from recon. The only genuinely one-way doors are Delivered and
    /// Archived, because a delivered vehicle is gone and unwinding that is a deal
    /// operation with its own paperwork, not a status flip on a vehicle.
    /// </remarks>
    private static bool IsTransitionAllowed(VehicleStatus from, VehicleStatus to) => from switch
    {
        VehicleStatus.Acquired => to is VehicleStatus.InRecon or VehicleStatus.Available
            or VehicleStatus.OnHold or VehicleStatus.Wholesaled or VehicleStatus.Archived,

        VehicleStatus.InRecon => to is VehicleStatus.Available or VehicleStatus.OnHold
            or VehicleStatus.Acquired or VehicleStatus.Wholesaled or VehicleStatus.Archived,

        VehicleStatus.Available => to is VehicleStatus.PendingSale or VehicleStatus.OnHold
            or VehicleStatus.InRecon or VehicleStatus.Wholesaled or VehicleStatus.Archived,

        VehicleStatus.OnHold => to is VehicleStatus.Available or VehicleStatus.InRecon
            or VehicleStatus.PendingSale or VehicleStatus.Wholesaled or VehicleStatus.Archived,

        // A deal falling through is ordinary, so PendingSale must be reversible.
        VehicleStatus.PendingSale => to is VehicleStatus.Sold or VehicleStatus.Available
            or VehicleStatus.OnHold,

        VehicleStatus.Sold => to is VehicleStatus.Delivered or VehicleStatus.Available,

        VehicleStatus.Delivered => to is VehicleStatus.Archived,

        VehicleStatus.Wholesaled => to is VehicleStatus.Archived,

        VehicleStatus.Archived => false,

        _ => false,
    };
}

/// <summary>Progress toward a publishable listing.</summary>
public sealed record PublishReadiness(int Satisfied, int Total, IReadOnlyList<string> Missing)
{
    public bool IsReady => Satisfied == Total;
}

/// <summary>Identity fields returned by a VIN decoder.</summary>
public sealed record VehicleDecodeResult(
    int? ModelYear,
    string? Make,
    string? Model,
    string? Trim,
    string? BodyStyle,
    string? DriveType,
    string? Engine,
    string? FuelType,
    string? Transmission);

public sealed record VehicleCreated(Guid VehicleId, Guid TenantId, string StockNumber, string? Vin)
    : DomainEvent
{
    public override string EventType => "inventory.vehicle.created";
}

public sealed record VehicleStatusChanged(Guid VehicleId, Guid TenantId, string From, string To)
    : DomainEvent
{
    public override string EventType => "inventory.vehicle.status_changed";
}

public sealed record VehiclePriceChanged(Guid VehicleId, Guid TenantId, decimal? From, decimal To)
    : DomainEvent
{
    public override string EventType => "inventory.vehicle.price_changed";
}

public sealed record VehiclePublished(Guid VehicleId, Guid TenantId) : DomainEvent
{
    public override string EventType => "inventory.vehicle.published";
}
