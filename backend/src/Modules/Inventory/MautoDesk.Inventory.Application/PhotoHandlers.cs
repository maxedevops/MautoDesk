using System.Security.Cryptography;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Application;

/* ------------------------------------------------------------------ ports -- */

public interface IPhotoRepository
{
    public void Add(VehiclePhoto photo);

    public Task<VehiclePhoto?> GetAsync(Guid photoId, CancellationToken cancellationToken);

    /// <summary>Every photo of a vehicle, ready or not, in display order.</summary>
    public Task<IReadOnlyList<VehiclePhoto>> ListAsync(Guid vehicleId, CancellationToken cancellationToken);

    public Task<int> CountReadyAsync(Guid vehicleId, CancellationToken cancellationToken);

    public Task<int> NextSortOrderAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <summary>A decoded image, re-encoded on our terms.</summary>
public sealed record ProcessedImage(byte[] Content, byte[] Thumbnail, int Width, int Height);

public interface IImageProcessor
{
    /// <summary>
    /// Decodes an image and re-encodes it, returning the full size and a thumbnail.
    /// </summary>
    /// <returns>Null when the bytes are not a decodable image.</returns>
    /// <remarks>
    /// A failure to decode is a rejection, not an exception: "this is not an
    /// image" is an ordinary thing for an upload to turn out to be.
    /// </remarks>
    public ProcessedImage? Process(Stream content);
}

/* --------------------------------------------------------------- commands -- */

public sealed record RequestPhotoUploadCommand(
    Guid VehicleId,
    string? ContentType,
    long ByteSize,
    string? Sha256);

public sealed record ConfirmPhotoUploadCommand(Guid VehicleId, Guid PhotoId);

public sealed record DeletePhotoCommand(Guid VehicleId, Guid PhotoId);

public sealed record SetPrimaryPhotoCommand(Guid VehicleId, Guid PhotoId);

/* -------------------------------------------------------------- handlers -- */

/// <summary>
/// The quarantine-first upload pipeline (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing the client says about a file is believed.</b> The declared type,
/// size, and digest are recorded when the upload is requested and then checked
/// against the object that actually arrived. A file that passes is re-encoded
/// before it is promoted, which is what removes EXIF, GPS, and anything hidden
/// in a file that is a valid image <em>and</em> something else.
/// </para>
/// <para>
/// The verification runs inline on confirm rather than in a background job. That
/// is a deliberate difference from ADR-0005's sketch: the outbox dispatcher does
/// not exist yet, and a photo that sits in "processing" forever because nothing
/// consumes the queue is worse than a confirm call that takes a second. The
/// seam — <see cref="ProcessAsync"/> — is a job body already, so moving it is a
/// change of caller, not of logic.
/// </para>
/// </remarks>
public sealed class PhotoCommandHandler
{
    /// <summary>Long enough for a phone on dealership wifi, short enough to be a capability.</summary>
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);

    private readonly IPhotoRepository _photos;
    private readonly IVehicleRepository _vehicles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IObjectStore _storage;
    private readonly IMalwareScanner _scanner;
    private readonly IImageProcessor _images;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    public PhotoCommandHandler(
        IPhotoRepository photos,
        IVehicleRepository vehicles,
        IUnitOfWork unitOfWork,
        IObjectStore storage,
        IMalwareScanner scanner,
        IImageProcessor images,
        ITenantContext tenant,
        IClock clock)
    {
        _photos = photos;
        _vehicles = vehicles;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _scanner = scanner;
        _images = images;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>Reserves a photo row and hands back a short-lived upload URL.</summary>
    public async Task<Result<PhotoUploadIntentDto>> RequestUploadAsync(
        RequestPhotoUploadCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.PhotoWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to add photos.");
        }

        var vehicle = await _vehicles.GetByIdAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        if (vehicle is null)
        {
            return Error.NotFound("vehicle.not_found", "That vehicle does not exist.");
        }

        var tenantId = _tenant.RequireTenantId();
        var sortOrder = await _photos.NextSortOrderAsync(command.VehicleId, cancellationToken)
            .ConfigureAwait(false);

        var photo = VehiclePhoto.Request(
            tenantId,
            command.VehicleId,
            command.ContentType,
            command.ByteSize,
            sortOrder,
            _tenant.UserId,
            _clock.UtcNow);

        if (photo.IsFailure)
        {
            return photo.Error!;
        }

        // The declared digest is kept for the confirm step to compare against.
        // It is a claim, not evidence, until the bytes are hashed.
        var declared = ParseDigest(command.Sha256);
        if (declared is null)
        {
            return Error.Validation(
                "photo.sha256",
                "A SHA-256 digest of the file is required, so the upload can be checked against it.",
                "sha256");
        }

        photo.Value.DeclareDigest(declared);
        _photos.Add(photo.Value);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var url = await _storage.CreateUploadUrlAsync(
            StorageBucket.Quarantine,
            photo.Value.ObjectKey,
            photo.Value.ContentType,
            photo.Value.ByteSize,
            UploadUrlLifetime,
            cancellationToken).ConfigureAwait(false);

        return new PhotoUploadIntentDto(
            photo.Value.Id,
            url.ToString(),
            (int)UploadUrlLifetime.TotalSeconds);
    }

    /// <summary>
    /// Verifies what actually landed in quarantine, then promotes or rejects it.
    /// </summary>
    public async Task<Result<VehiclePhotoDto>> ConfirmUploadAsync(
        ConfirmPhotoUploadCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.PhotoWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to add photos.");
        }

        var photo = await _photos.GetAsync(command.PhotoId, cancellationToken).ConfigureAwait(false);

        if (photo is null || photo.VehicleId != command.VehicleId)
        {
            return Error.NotFound("photo.not_found", "That photo does not exist.");
        }

        if (photo.IsReady)
        {
            // Confirming twice is a retry, not an error — the client may not
            // have seen the first response.
            return await DescribeAsync(photo, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await ProcessAsync(photo, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        return await DescribeAsync(photo, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(DeletePhotoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.PhotoWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to remove photos.");
        }

        var photo = await _photos.GetAsync(command.PhotoId, cancellationToken).ConfigureAwait(false);

        if (photo is null || photo.VehicleId != command.VehicleId)
        {
            return Error.NotFound("photo.not_found", "That photo does not exist.");
        }

        photo.Delete(_clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The row is soft-deleted for the audit trail; the object is not kept.
        // A photo of the wrong car, or of someone's driveway, should stop being
        // fetchable the moment it is removed.
        await SafeDeleteAsync(StorageBucket.Media, photo.ObjectKey, cancellationToken).ConfigureAwait(false);

        if (photo.ThumbnailKey is { } thumbnail)
        {
            await SafeDeleteAsync(StorageBucket.Media, thumbnail, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result> SetPrimaryAsync(
        SetPrimaryPhotoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_tenant.HasPermission(InventoryPermissions.PhotoWrite))
        {
            return Error.Forbidden("forbidden", "You do not have permission to change photos.");
        }

        var photos = await _photos.ListAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
        var target = photos.FirstOrDefault(p => p.Id == command.PhotoId);

        if (target is null)
        {
            return Error.NotFound("photo.not_found", "That photo does not exist.");
        }

        if (!target.IsReady)
        {
            return Error.Conflict(
                "photo.not_ready",
                "That photo is still being checked. Try again in a moment.");
        }

        var now = _clock.UtcNow;

        // Cleared first: the unique index allows one primary per vehicle, so
        // setting the new one before clearing the old one violates it.
        foreach (var other in photos.Where(p => p.IsPrimary && p.Id != target.Id))
        {
            other.ClearPrimary(now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        target.MakePrimary(now);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// The verification itself: the part that decides whether bytes become a photo.
    /// </summary>
    /// <remarks>
    /// In order, and every step is a rejection rather than an exception:
    /// the object exists; its length matches what was declared; its digest
    /// matches what was declared; it is free of known malware; it decodes as an
    /// image; and the re-encoded result is what gets stored. The quarantine
    /// object is deleted either way.
    /// </remarks>
    private async Task<Result> ProcessAsync(VehiclePhoto photo, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var quarantineKey = photo.ObjectKey;

        var stored = await _storage.StatAsync(StorageBucket.Quarantine, quarantineKey, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            photo.Reject("The file was never uploaded.", now);
            return Error.Conflict("photo.missing", "No file was uploaded for this photo.");
        }

        if (stored.ByteSize != photo.ByteSize)
        {
            await RejectAsync(
                photo,
                $"The uploaded file is {stored.ByteSize} bytes; {photo.ByteSize} was declared.",
                quarantineKey,
                now,
                cancellationToken).ConfigureAwait(false);

            return Error.Validation("photo.size_mismatch", "The uploaded file did not match what was declared.");
        }

        photo.BeginScanning(now);

        byte[] bytes;

        await using (var source = await _storage.OpenAsync(StorageBucket.Quarantine, quarantineKey, cancellationToken)
            .ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var actualDigest = SHA256.HashData(bytes);

        if (!CryptographicOperations.FixedTimeEquals(actualDigest, photo.Sha256))
        {
            await RejectAsync(
                photo,
                "The uploaded file does not match the digest that was declared.",
                quarantineKey,
                now,
                cancellationToken).ConfigureAwait(false);

            return Error.Validation(
                "photo.digest_mismatch",
                "The uploaded file did not match what was declared. Try the upload again.");
        }

        using (var forScanning = new MemoryStream(bytes, writable: false))
        {
            var verdict = await _scanner.ScanAsync(forScanning, cancellationToken).ConfigureAwait(false);

            if (!verdict.IsClean)
            {
                await RejectAsync(
                    photo,
                    $"The file was rejected by the malware scanner ({verdict.Threat}).",
                    quarantineKey,
                    now,
                    cancellationToken).ConfigureAwait(false);

                return Error.Validation("photo.infected", "That file was rejected by the malware scanner.");
            }
        }

        photo.BeginProcessing(now);

        ProcessedImage? processed;

        using (var forDecoding = new MemoryStream(bytes, writable: false))
        {
            processed = _images.Process(forDecoding);
        }

        if (processed is null)
        {
            await RejectAsync(
                photo,
                "The file is not a readable image.",
                quarantineKey,
                now,
                cancellationToken).ConfigureAwait(false);

            return Error.Validation(
                "photo.not_an_image",
                "That file is not a readable image, whatever it was named.");
        }

        var mediaKey = VehiclePhoto.MediaKey(photo.TenantId, photo.VehicleId, photo.Id);
        var thumbnailKey = VehiclePhoto.MediaKey(photo.TenantId, photo.VehicleId, photo.Id, thumbnail: true);

        using (var content = new MemoryStream(processed.Content, writable: false))
        {
            await _storage.PutAsync(StorageBucket.Media, mediaKey, content, "image/jpeg", cancellationToken)
                .ConfigureAwait(false);
        }

        using (var thumbnail = new MemoryStream(processed.Thumbnail, writable: false))
        {
            await _storage.PutAsync(StorageBucket.Media, thumbnailKey, thumbnail, "image/jpeg", cancellationToken)
                .ConfigureAwait(false);
        }

        photo.Promote(
            mediaKey,
            thumbnailKey,
            processed.Content.LongLength,
            processed.Width,
            processed.Height,
            actualDigest,
            now);

        // Quarantine is not a backup. Leaving the original behind would keep the
        // un-sanitized file — EXIF, GPS, and all — reachable by anything that can
        // read the bucket.
        await SafeDeleteAsync(StorageBucket.Quarantine, quarantineKey, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task RejectAsync(
        VehiclePhoto photo,
        string reason,
        string quarantineKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        photo.Reject(reason, now);
        await SafeDeleteAsync(StorageBucket.Quarantine, quarantineKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an object without letting a storage failure undo a database decision.
    /// </summary>
    /// <remarks>
    /// The row has already been rejected or removed by the time this runs. If
    /// the delete fails, the quarantine bucket's lifecycle rule expires the
    /// object within a day anyway — whereas throwing here would turn a
    /// successful rejection into a 500 and invite the client to retry an upload
    /// that was correctly refused.
    /// </remarks>
    private async Task SafeDeleteAsync(
        StorageBucket bucket,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Deliberately swallowed. See the remarks above.
        }
    }

    private async Task<VehiclePhotoDto> DescribeAsync(VehiclePhoto photo, CancellationToken cancellationToken) =>
        await PhotoQueryHandler
            .DescribeAsync(photo, _storage, cancellationToken)
            .ConfigureAwait(false);

    private static byte[]? ParseDigest(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Photo reads.</summary>
public sealed class PhotoQueryHandler
{
    /// <summary>
    /// Long enough to load a page, short enough that a leaked URL is not a leak.
    /// </summary>
    /// <remarks>
    /// Photos are not public until the vehicle is published, and even then they
    /// are served through the CDN rather than from a signed origin URL. This
    /// lifetime is for the dealer's own screens.
    /// </remarks>
    private static readonly TimeSpan ViewUrlLifetime = TimeSpan.FromMinutes(30);

    private readonly IPhotoRepository _photos;
    private readonly IVehicleRepository _vehicles;
    private readonly IObjectStore _storage;
    private readonly ITenantContext _tenant;

    public PhotoQueryHandler(
        IPhotoRepository photos,
        IVehicleRepository vehicles,
        IObjectStore storage,
        ITenantContext tenant)
    {
        _photos = photos;
        _vehicles = vehicles;
        _storage = storage;
        _tenant = tenant;
    }

    public async Task<Result<IReadOnlyList<VehiclePhotoDto>>> ListAsync(
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        if (!_tenant.HasPermission(InventoryPermissions.VehicleRead))
        {
            return Error.Forbidden("forbidden", "You do not have permission to view this vehicle.");
        }

        // 404 for a vehicle this tenant cannot see, rather than an empty list.
        // An empty list and a foreign vehicle would be distinguishable from a
        // vehicle that simply has no photos yet — which turns this route into an
        // existence oracle for another dealership's stock.
        var vehicle = await _vehicles.GetByIdAsync(vehicleId, cancellationToken).ConfigureAwait(false);

        if (vehicle is null)
        {
            return Error.NotFound("vehicle.not_found", "That vehicle does not exist.");
        }

        var photos = await _photos.ListAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        var described = new List<VehiclePhotoDto>(photos.Count);

        foreach (var photo in photos)
        {
            described.Add(await DescribeAsync(photo, _storage, cancellationToken).ConfigureAwait(false));
        }

        return described;
    }

    internal static async Task<VehiclePhotoDto> DescribeAsync(
        VehiclePhoto photo,
        IObjectStore storage,
        CancellationToken cancellationToken)
    {
        // A URL is only minted for a photo that passed every check. A pending or
        // rejected row is still returned — the UI shows it as failed — but there
        // is deliberately nothing to fetch.
        string? url = null;
        string? thumbnailUrl = null;

        if (photo.IsReady)
        {
            url = (await storage
                .CreateDownloadUrlAsync(StorageBucket.Media, photo.ObjectKey, ViewUrlLifetime, cancellationToken)
                .ConfigureAwait(false)).ToString();

            if (photo.ThumbnailKey is { } thumbnailKey)
            {
                thumbnailUrl = (await storage
                    .CreateDownloadUrlAsync(StorageBucket.Media, thumbnailKey, ViewUrlLifetime, cancellationToken)
                    .ConfigureAwait(false)).ToString();
            }
        }

        return new VehiclePhotoDto(
            photo.Id,
            url,
            thumbnailUrl,
            photo.Width,
            photo.Height,
            photo.IsPrimary,
            photo.SortOrder,
            photo.Caption,
            photo.ProcessingStatus switch
            {
                PhotoProcessingStatus.Pending => "pending",
                PhotoProcessingStatus.Scanning => "scanning",
                PhotoProcessingStatus.Processing => "processing",
                PhotoProcessingStatus.Ready => "ready",
                PhotoProcessingStatus.Rejected => "rejected",
                _ => "pending",
            },
            photo.RejectionReason);
    }
}
