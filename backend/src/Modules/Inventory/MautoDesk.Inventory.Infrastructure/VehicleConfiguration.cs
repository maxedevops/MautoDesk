using System.Reflection;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Inventory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MautoDesk.Inventory.Infrastructure;

/// <summary>Registers this module's EF configurations with the shared context.</summary>
public sealed class InventorySchema : IModuleSchema
{
    public Assembly ConfigurationAssembly => typeof(InventorySchema).Assembly;
}

/// <summary>
/// Maps <see cref="Vehicle"/> onto the <c>inventory.vehicle</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written, because <c>db/migrations</c> is the source of truth for the
/// schema and EF generates no migrations (ADR-0011). Everything that makes this
/// schema safe — row-level security, append-only triggers, generated tsvector
/// columns, partial indexes — is something EF migrations model poorly or not at
/// all. The trade is that the model can drift from the database, which is closed
/// by <c>ModelMatchesDatabaseTests</c> rather than by hope.
/// </para>
/// <para>
/// Concurrency uses PostgreSQL's system <c>xmin</c> column. No hand-maintained
/// version column means nothing to forget in a raw-SQL write path.
/// </para>
/// </remarks>
public sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("vehicle", "inventory");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("id");
        builder.Property(v => v.TenantId).HasColumnName("tenant_id");
        builder.Property(v => v.StockNumber).HasColumnName("stock_number").IsRequired();
        builder.Property(v => v.Vin).HasColumnName("vin").HasMaxLength(17);
        builder.Property(v => v.ModelYear).HasColumnName("model_year");
        builder.Property(v => v.Make).HasColumnName("make");
        builder.Property(v => v.Model).HasColumnName("model");
        builder.Property(v => v.Trim).HasColumnName("trim");
        builder.Property(v => v.BodyStyle).HasColumnName("body_style");
        builder.Property(v => v.DriveType).HasColumnName("drive_type");
        builder.Property(v => v.Engine).HasColumnName("engine");
        builder.Property(v => v.FuelType).HasColumnName("fuel_type");
        builder.Property(v => v.Transmission).HasColumnName("transmission");
        builder.Property(v => v.ExteriorColor).HasColumnName("exterior_color");
        builder.Property(v => v.InteriorColor).HasColumnName("interior_color");
        builder.Property(v => v.Mileage).HasColumnName("mileage");

        // Stored as the snake_case string the database check constraint expects,
        // never as an ordinal. An int would silently remap every status the day
        // someone inserts a new enum member in the middle.
        builder.Property(v => v.Status)
            .HasColumnName("status")
            .HasConversion(
                status => ToDatabase(status),
                value => FromDatabase(value))
            .IsRequired();

        builder.Property(v => v.ListPrice).HasColumnName("list_price").HasColumnType("numeric(14,2)");
        builder.Property(v => v.Description).HasColumnName("description");
        builder.Property(v => v.AiDescriptionDraft).HasColumnName("ai_description_draft");
        builder.Property(v => v.AiDescriptionApprovedAt).HasColumnName("ai_description_approved_at");
        builder.Property(v => v.AcquiredAt).HasColumnName("acquired_at");
        builder.Property(v => v.AvailableAt).HasColumnName("available_at");
        builder.Property(v => v.SoldAt).HasColumnName("sold_at");
        builder.Property(v => v.IsPublished).HasColumnName("is_published");
        builder.Property(v => v.Location).HasColumnName("location");
        builder.Property(v => v.Notes).HasColumnName("notes");

        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.CreatedBy).HasColumnName("created_by");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by");
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");
        builder.Property(v => v.DeletedBy).HasColumnName("deleted_by");

        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.Ignore(v => v.DomainEvents);

        // Convenience, not a security control. RLS is what actually enforces
        // tenancy; this filter keeps soft-deleted rows out of ordinary queries
        // and catches mistakes at development time. Anything relying on it for
        // isolation would break the moment someone calls IgnoreQueryFilters.
        builder.HasQueryFilter(v => v.DeletedAt == null);
    }

    private static string ToDatabase(VehicleStatus status) => status switch
    {
        VehicleStatus.Acquired => "acquired",
        VehicleStatus.InRecon => "in_recon",
        VehicleStatus.Available => "available",
        VehicleStatus.OnHold => "on_hold",
        VehicleStatus.PendingSale => "pending_sale",
        VehicleStatus.Sold => "sold",
        VehicleStatus.Delivered => "delivered",
        VehicleStatus.Wholesaled => "wholesaled",
        VehicleStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped vehicle status."),
    };

    private static VehicleStatus FromDatabase(string value) => value switch
    {
        "acquired" => VehicleStatus.Acquired,
        "in_recon" => VehicleStatus.InRecon,
        "available" => VehicleStatus.Available,
        "on_hold" => VehicleStatus.OnHold,
        "pending_sale" => VehicleStatus.PendingSale,
        "sold" => VehicleStatus.Sold,
        "delivered" => VehicleStatus.Delivered,
        "wholesaled" => VehicleStatus.Wholesaled,
        "archived" => VehicleStatus.Archived,

        // Loudly, not silently. A status present in the database but unknown to
        // the model means a migration shipped ahead of the code, and quietly
        // defaulting to Acquired would corrupt inventory reporting.
        _ => throw new InvalidOperationException(
            $"'{value}' is not a status this build understands. A migration may have shipped ahead of the code."),
    };
}
