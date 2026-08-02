using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using Microsoft.AspNetCore.Mvc;

namespace MautoDesk.Api;

/// <summary>
/// Inventory HTTP surface, implementing <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// These are thin on purpose. An endpoint parses the request, calls a handler,
/// and maps the result — it contains no business rules and, critically, no
/// authorization checks. Permissions are enforced in the application layer so
/// that a background job or an internal call is gated by exactly the same code
/// (docs/02-architecture.md §5).
/// </remarks>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Authentication is required at the group level, so an unauthenticated
        // caller gets 401 rather than falling through to a permission check that
        // would report 403. Authorization — which permission — stays in the
        // application layer so jobs and internal callers are gated identically.
        var vehicles = app.MapGroup("/api/v1/vehicles")
            .WithTags("Inventory")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.ReadPolicy);

        vehicles.MapGet("/", ListVehiclesAsync)
            .WithName("listVehicles")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleRead))
            .WithSummary("List vehicles")
            .Produces<PagedResult<VehicleSummaryDto>>(StatusCodes.Status200OK);

        vehicles.MapPost("/", CreateVehicleAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("createVehicle")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleWrite))
            .WithSummary("Create a vehicle")
            .Produces<VehicleDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        vehicles.MapGet("/{vehicleId:guid}", GetVehicleAsync)
            .WithName("getVehicle")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleRead))
            .WithSummary("Get a vehicle")
            .Produces<VehicleDto>(StatusCodes.Status200OK);

        vehicles.MapDelete("/{vehicleId:guid}", DeleteVehicleAsync)
            .WithName("deleteVehicle")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleDelete))
            .WithSummary("Soft-delete a vehicle")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        vehicles.MapPost("/{vehicleId:guid}/status", ChangeStatusAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("changeVehicleStatus")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleWrite))
            .WithSummary("Change vehicle status")
            .Produces<VehicleDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict);

        vehicles.MapPost("/{vehicleId:guid}/price", ChangePriceAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("changeVehiclePrice")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.PriceWrite))
            .WithSummary("Change an asking price")
            .Produces<VehicleDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        vehicles.MapPost("/{vehicleId:guid}/publish", PublishAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("publishVehicle")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.Publish))
            .WithSummary("Publish to the website and syndication channels")
            .Produces<VehicleDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        app.MapGet("/api/v1/vin/{vin}/decode", DecodeVinAsync)
            .RequireAuthorization()
            .WithTags("Inventory")
            .WithName("decodeVin")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleRead))
            .WithSummary("Decode a VIN")
            .Produces<VinDecodeDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> ListVehiclesAsync(
        [FromServices] VehicleQueryHandler handler,
        HttpContext context,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = VehicleListFilter.DefaultPageSize,
        [FromQuery] string? sort = null,
        [FromQuery] string? q = null,
        [FromQuery] string[]? status = null,
        [FromQuery] string? make = null,
        [FromQuery] string? model = null,
        [FromQuery] bool? isPublished = null,
        [FromQuery] int? ageDaysMin = null,
        CancellationToken cancellationToken = default)
    {
        // An unrecognized status is dropped rather than rejected. A client
        // sending a filter this build does not know about should get a sensible
        // page, not a 422 that breaks their screen during a rolling deploy.
        var statuses = status?
            .Select(value => Enum.TryParse<VehicleStatus>(
                value.Replace("_", string.Empty, StringComparison.Ordinal),
                ignoreCase: true,
                out var parsed)
                ? parsed
                : (VehicleStatus?)null)
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!.Value)
            .ToList();

        var filter = new VehicleListFilter
        {
            Page = page,
            PageSize = pageSize,
            Sort = sort,
            Search = q,
            Statuses = statuses,
            Make = make,
            Model = model,
            IsPublished = isPublished,
            AgeDaysMin = ageDaysMin,
        };

        var result = await handler.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return result.ToHttp(context);
    }

    private static async Task<IResult> CreateVehicleAsync(
        [FromServices] VehicleCommandHandler handler,
        [FromBody] CreateVehicleCommand command,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.CreateAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/vehicles/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem(context);
    }

    private static async Task<IResult> GetVehicleAsync(
        [FromServices] VehicleQueryHandler handler,
        Guid vehicleId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        return result.ToHttp(context);
    }

    private static async Task<IResult> DeleteVehicleAsync(
        [FromServices] VehicleCommandHandler handler,
        Guid vehicleId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler.DeleteAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem(context);
    }

    private static async Task<IResult> ChangeStatusAsync(
        [FromServices] VehicleCommandHandler handler,
        Guid vehicleId,
        [FromBody] ChangeStatusRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await handler
            .ChangeStatusAsync(new ChangeVehicleStatusCommand(vehicleId, request.Status), cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> ChangePriceAsync(
        [FromServices] VehicleCommandHandler handler,
        Guid vehicleId,
        [FromBody] ChangePriceRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await handler
            .SetPriceAsync(
                new SetVehiclePriceCommand(vehicleId, request.NewPrice, request.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> PublishAsync(
        [FromServices] VehicleCommandHandler handler,
        Guid vehicleId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var result = await handler
            .PublishAsync(new PublishVehicleCommand(vehicleId), cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> DecodeVinAsync(
        [FromServices] VehicleQueryHandler handler,
        string vin,
        HttpContext context,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.DecodeVinAsync(vin, refresh, cancellationToken).ConfigureAwait(false);
        return result.ToHttp(context);
    }

    /// <summary>Body of a status change.</summary>
    public sealed record ChangeStatusRequest(string Status, string? Reason);

    /// <summary>Body of a price change. The price is a decimal string, never a number.</summary>
    public sealed record ChangePriceRequest(string PriceType, string NewPrice, string? Reason);
}
