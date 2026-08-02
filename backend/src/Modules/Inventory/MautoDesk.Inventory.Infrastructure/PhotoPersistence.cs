using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MautoDesk.Inventory.Infrastructure;

public sealed class VehiclePhotoConfiguration : IEntityTypeConfiguration<VehiclePhoto>
{
    public void Configure(EntityTypeBuilder<VehiclePhoto> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("vehicle_photo", "inventory");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.TenantId).HasColumnName("tenant_id");
        builder.Property(p => p.VehicleId).HasColumnName("vehicle_id");
        builder.Property(p => p.ObjectKey).HasColumnName("object_key");
        builder.Property(p => p.ThumbnailKey).HasColumnName("thumbnail_key");
        builder.Property(p => p.ContentType).HasColumnName("content_type");
        builder.Property(p => p.ByteSize).HasColumnName("byte_size");
        builder.Property(p => p.Width).HasColumnName("width");
        builder.Property(p => p.Height).HasColumnName("height");
        builder.Property(p => p.Sha256).HasColumnName("sha256");
        builder.Property(p => p.SortOrder).HasColumnName("sort_order");
        builder.Property(p => p.Caption).HasColumnName("caption");
        builder.Property(p => p.IsPrimary).HasColumnName("is_primary");
        builder.Property(p => p.RejectionReason).HasColumnName("rejection_reason");
        builder.Property(p => p.ExifStripped).HasColumnName("exif_stripped");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.CreatedBy).HasColumnName("created_by");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Property(p => p.ProcessingStatus)
            .HasColumnName("processing_status")
            .HasConversion(status => ToWire(status), value => Parse(value));

        // Soft deletes are invisible by default. A removed photo stays in the
        // table for the audit trail but must never come back on a listing.
        builder.HasQueryFilter(p => p.DeletedAt == null);
    }

    internal static string ToWire(PhotoProcessingStatus status) => status switch
    {
        PhotoProcessingStatus.Pending => "pending",
        PhotoProcessingStatus.Scanning => "scanning",
        PhotoProcessingStatus.Processing => "processing",
        PhotoProcessingStatus.Ready => "ready",
        PhotoProcessingStatus.Rejected => "rejected",
        _ => throw new InvalidOperationException($"'{status}' is not a photo status this build understands."),
    };

    private static PhotoProcessingStatus Parse(string value) => value switch
    {
        "pending" => PhotoProcessingStatus.Pending,
        "scanning" => PhotoProcessingStatus.Scanning,
        "processing" => PhotoProcessingStatus.Processing,
        "ready" => PhotoProcessingStatus.Ready,
        "rejected" => PhotoProcessingStatus.Rejected,
        _ => throw new InvalidOperationException($"'{value}' is not a photo status this build understands."),
    };
}

/// <summary>Photo reads and writes, scoped by RLS like everything else.</summary>
public sealed class PhotoRepository : IPhotoRepository
{
    private readonly MautoDeskDbContext _db;

    public PhotoRepository(MautoDeskDbContext db) => _db = db;

    public void Add(VehiclePhoto photo) => _db.Set<VehiclePhoto>().Add(photo);

    public Task<VehiclePhoto?> GetAsync(Guid photoId, CancellationToken cancellationToken) =>
        _db.Set<VehiclePhoto>().FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken);

    public async Task<IReadOnlyList<VehiclePhoto>> ListAsync(
        Guid vehicleId,
        CancellationToken cancellationToken) =>
        await _db.Set<VehiclePhoto>()
            .Where(p => p.VehicleId == vehicleId)
            // Primary first, then the dealer's chosen order. A listing's lead
            // photo is the one thing that decides whether anyone clicks.
            .OrderByDescending(p => p.IsPrimary)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountReadyAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        _db.Set<VehiclePhoto>()
            .CountAsync(
                p => p.VehicleId == vehicleId && p.ProcessingStatus == PhotoProcessingStatus.Ready,
                cancellationToken);

    public async Task<int> NextSortOrderAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var highest = await _db.Set<VehiclePhoto>()
            .Where(p => p.VehicleId == vehicleId)
            .Select(p => (int?)p.SortOrder)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        return (highest ?? -1) + 1;
    }
}
