using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Domain;

/// <summary>Where a photo is in the quarantine-first pipeline.</summary>
public enum PhotoProcessingStatus
{
    /// <summary>A row exists and an upload URL was handed out. Nothing verified.</summary>
    Pending,

    Scanning,

    Processing,

    /// <summary>Verified, re-encoded, promoted. The only status that is ever shown.</summary>
    Ready,

    /// <summary>Failed a check. The reason is kept; the object is gone.</summary>
    Rejected,
}

/// <summary>
/// A photo of a vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row here is not a photo the dealer has.</b> It is created when an upload
/// is requested, before any bytes exist, and only reaches <see
/// cref="PhotoProcessingStatus.Ready"/> once the object in quarantine has been
/// checked against what the client declared and re-encoded. Everything that
/// reads photos filters on ready, or it will show a file nobody has verified.
/// </para>
/// <para>
/// This matters more than it looks: photos are the one thing on this system that
/// a member of the public will fetch, and an image is a perfectly good container
/// for something that is also a script.
/// </para>
/// </remarks>
public sealed class VehiclePhoto : Entity
{
    /// <summary>The types a dealer's phone or camera actually produces.</summary>
    /// <remarks>
    /// An allowlist, not a blocklist. Everything here is re-encoded to JPEG on
    /// promotion regardless, so this only decides what we are willing to decode.
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
        };

    /// <summary>20 MB. Comfortably above a phone photo, well below a denial of service.</summary>
    public const long MaxByteSize = 20 * 1024 * 1024;

    private VehiclePhoto(Guid id, Guid tenantId, Guid vehicleId)
        : base(id)
    {
        TenantId = tenantId;
        VehicleId = vehicleId;
    }

    /// <summary>Required by EF Core materialization.</summary>
    private VehiclePhoto()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid VehicleId { get; private set; }

    /// <summary>Where the object lives: the quarantine key first, the media key after promotion.</summary>
    public string ObjectKey { get; private set; } = string.Empty;

    public string? ThumbnailKey { get; private set; }

    /// <summary>What the client said it was sending, then what we verified it to be.</summary>
    public string ContentType { get; private set; } = string.Empty;

    public long ByteSize { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    /// <summary>The verified digest of the bytes that were uploaded.</summary>
    public byte[] Sha256 { get; private set; } = [];

    public int SortOrder { get; private set; }

    public string? Caption { get; private set; }

    public bool IsPrimary { get; private set; }

    public PhotoProcessingStatus ProcessingStatus { get; private set; } = PhotoProcessingStatus.Pending;

    public string? RejectionReason { get; private set; }

    public bool ExifStripped { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsReady => ProcessingStatus == PhotoProcessingStatus.Ready && DeletedAt is null;

    /// <summary>
    /// Reserves a photo slot and records what the client claims it will send.
    /// </summary>
    /// <remarks>
    /// The declared values are stored so the confirm step has something to check
    /// the object against. They are never trusted on their own — a client that
    /// declares "image/jpeg, 2 MB" and uploads a 2 GB executable fails at
    /// exactly that comparison.
    /// </remarks>
    public static Result<VehiclePhoto> Request(
        Guid tenantId,
        Guid vehicleId,
        string? contentType,
        long byteSize,
        int sortOrder,
        Guid? requestedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType))
        {
            return Error.Validation(
                "photo.content_type",
                "Photos must be JPEG, PNG, or WebP.",
                "contentType");
        }

        if (byteSize <= 0)
        {
            return Error.Validation("photo.size", "The file appears to be empty.", "byteSize");
        }

        if (byteSize > MaxByteSize)
        {
            return Error.Validation(
                "photo.size",
                $"Photos must be {MaxByteSize / (1024 * 1024)} MB or smaller.",
                "byteSize");
        }

        var id = Guid.CreateVersion7();

        return new VehiclePhoto(id, tenantId, vehicleId)
        {
            ObjectKey = QuarantineKey(tenantId, vehicleId, id),
            ContentType = contentType.ToLowerInvariant(),
            ByteSize = byteSize,
            SortOrder = sortOrder,
            CreatedBy = requestedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The quarantine object key. Tenant-prefixed so a bucket listing is still scoped.</summary>
    public static string QuarantineKey(Guid tenantId, Guid vehicleId, Guid photoId) =>
        $"quarantine/{tenantId}/{vehicleId}/{photoId}";

    /// <summary>The key a promoted photo is served from.</summary>
    public static string MediaKey(Guid tenantId, Guid vehicleId, Guid photoId, bool thumbnail = false) =>
        $"{tenantId}/vehicles/{vehicleId}/{photoId}{(thumbnail ? "-thumb" : string.Empty)}.jpg";

    /// <summary>
    /// Records the digest the client says the file has.
    /// </summary>
    /// <remarks>
    /// Stored in the same column the verified digest ends up in, because until
    /// the bytes arrive there is nothing better to put there and the column is
    /// not nullable. It is a claim until <see cref="Promote"/> overwrites it
    /// with a digest we computed ourselves.
    /// </remarks>
    public void DeclareDigest(byte[] sha256) => Sha256 = sha256;

    public void BeginScanning(DateTimeOffset now)
    {
        ProcessingStatus = PhotoProcessingStatus.Scanning;
        UpdatedAt = now;
    }

    public void BeginProcessing(DateTimeOffset now)
    {
        ProcessingStatus = PhotoProcessingStatus.Processing;
        UpdatedAt = now;
    }

    /// <summary>Records the verified, re-encoded result and makes the photo visible.</summary>
    public void Promote(
        string objectKey,
        string? thumbnailKey,
        long byteSize,
        int width,
        int height,
        byte[] sha256,
        DateTimeOffset now)
    {
        ObjectKey = objectKey;
        ThumbnailKey = thumbnailKey;
        ContentType = "image/jpeg";
        ByteSize = byteSize;
        Width = width;
        Height = height;
        Sha256 = sha256;

        // Re-encoding is what strips EXIF, so this is a statement of fact about
        // the stored object rather than an intention. A dealer's photo carries
        // GPS coordinates of the lot, and sometimes of their house.
        ExifStripped = true;
        ProcessingStatus = PhotoProcessingStatus.Ready;
        RejectionReason = null;
        UpdatedAt = now;
    }

    /// <summary>Fails the photo with a reason a person can act on.</summary>
    public void Reject(string reason, DateTimeOffset now)
    {
        ProcessingStatus = PhotoProcessingStatus.Rejected;
        RejectionReason = reason;
        UpdatedAt = now;
    }

    public void MakePrimary(DateTimeOffset now)
    {
        IsPrimary = true;
        UpdatedAt = now;
    }

    public void ClearPrimary(DateTimeOffset now)
    {
        IsPrimary = false;
        UpdatedAt = now;
    }

    public void Reorder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    /// <summary>Soft-deletes. The object is removed separately.</summary>
    public void Delete(DateTimeOffset now)
    {
        DeletedAt = now;
        IsPrimary = false;
        UpdatedAt = now;
    }
}
