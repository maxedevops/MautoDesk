using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace MautoDesk.Api;

/// <summary>
/// The photo surface: a three-step upload, then ordinary management.
/// </summary>
/// <remarks>
/// The client asks for permission to upload, PUTs the file straight to the
/// quarantine bucket, then confirms. The bytes never pass through this API —
/// which keeps a 20 MB upload off the request pipeline — but nothing about the
/// file is trusted until confirm has checked the object against what was
/// declared (ADR-0005).
/// </remarks>
public static class PhotoEndpoints
{
    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var photos = app.MapGroup("/api/v1/vehicles/{vehicleId:guid}/photos")
            .WithTags("Inventory")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.ReadPolicy);

        photos.MapGet("/", ListAsync)
            .WithName("listVehiclePhotos")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.VehicleRead))
            .WithSummary("List a vehicle's photos")
            .WithDescription(
                "Includes photos that are still being checked and photos that were rejected, so the " +
                "screen can say what happened. Only a photo with status 'ready' carries a URL.")
            .Produces<IReadOnlyList<VehiclePhotoDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        photos.MapPost("/", RequestUploadAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("requestPhotoUpload")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.PhotoWrite))
            .WithSummary("Request permission to upload one photo")
            .WithDescription(
                "Returns a short-lived, single-object URL for a PUT to the quarantine bucket. The " +
                "declared content type, byte size, and SHA-256 are recorded and checked against the " +
                "object that actually arrives; a mismatch is a rejection.")
            .Produces<PhotoUploadIntentDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        photos.MapPost("/{photoId:guid}/confirm", ConfirmAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("confirmPhotoUpload")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.PhotoWrite))
            .WithSummary("Verify an uploaded photo and publish it to the media bucket")
            .WithDescription(
                "Checks size, digest, and malware, decodes the image, re-encodes it — which is what " +
                "strips EXIF and GPS — and promotes the result. Any failure rejects the photo and " +
                "deletes the quarantined object. Confirming an already-verified photo is a no-op.")
            .Produces<VehiclePhotoDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        photos.MapPost("/{photoId:guid}/primary", SetPrimaryAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("setPrimaryPhoto")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.PhotoWrite))
            .WithSummary("Make a photo the lead image for the listing")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        photos.MapDelete("/{photoId:guid}", DeleteAsync)
            .RequireRateLimiting(RateLimiting.WritePolicy)
            .WithName("deleteVehiclePhoto")
            .WithMetadata(new RequiresPermissionAttribute(InventoryPermissions.PhotoWrite))
            .WithSummary("Remove a photo")
            .WithDescription(
                "The row is kept for the audit trail; the stored object is deleted, so a photo of " +
                "the wrong car stops being fetchable immediately.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListAsync(
        [FromServices] PhotoQueryHandler handler,
        Guid vehicleId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var result = await handler.ListAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        return result.ToHttp(context);
    }

    private static async Task<IResult> RequestUploadAsync(
        [FromServices] PhotoCommandHandler handler,
        Guid vehicleId,
        [FromBody] RequestUploadRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(request);

        var result = await handler
            .RequestUploadAsync(
                new RequestPhotoUploadCommand(vehicleId, request.ContentType, request.ByteSize, request.Sha256),
                cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> ConfirmAsync(
        [FromServices] PhotoCommandHandler handler,
        Guid vehicleId,
        Guid photoId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var result = await handler
            .ConfirmUploadAsync(new ConfirmPhotoUploadCommand(vehicleId, photoId), cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> SetPrimaryAsync(
        [FromServices] PhotoCommandHandler handler,
        Guid vehicleId,
        Guid photoId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var result = await handler
            .SetPrimaryAsync(new SetPrimaryPhotoCommand(vehicleId, photoId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? result.Error!.ToProblem(context) : TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        [FromServices] PhotoCommandHandler handler,
        Guid vehicleId,
        Guid photoId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var result = await handler
            .DeleteAsync(new DeletePhotoCommand(vehicleId, photoId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? result.Error!.ToProblem(context) : TypedResults.NoContent();
    }

    /// <summary>What the client claims it is about to upload.</summary>
    /// <remarks>
    /// Every field here is a claim, checked at confirm against the object that
    /// arrived. The digest is required rather than optional: without it there is
    /// nothing to prove the bytes we verified are the bytes the client sent.
    /// </remarks>
    public sealed record RequestUploadRequest(string? ContentType, long ByteSize, string? Sha256);
}
