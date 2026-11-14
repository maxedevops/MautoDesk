using System.Globalization;
using System.Reflection;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Sales.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MautoDesk.Sales.Infrastructure;

/// <summary>Registers this module's EF configurations with the shared context.</summary>
public sealed class SalesSchema : IModuleSchema
{
    /// <inheritdoc />
    public Assembly ConfigurationAssembly => typeof(SalesSchema).Assembly;
}

/// <summary>Maps <see cref="Deal"/> onto <c>sales.deal</c>.</summary>
/// <remarks>
/// Hand-written, because <c>db/migrations</c> owns the schema and EF generates no
/// migrations (ADR-0011). Note what is absent: not one monetary column. The money
/// lives on the snapshot, and a mapping that offered a <c>TotalSalePrice</c> here
/// would invite somebody to write to it.
/// </remarks>
public sealed class DealConfiguration : IEntityTypeConfiguration<Deal>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Deal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deal", "sales");
        builder.HasKey(deal => deal.Id);

        builder.Property(deal => deal.Id).HasColumnName("id");
        builder.Property(deal => deal.TenantId).HasColumnName("tenant_id");
        builder.Property(deal => deal.DealNumber).HasColumnName("deal_number").IsRequired();

        // Stored as the snake_case string the check constraint expects, never as an
        // ordinal. An int would silently remap every status the day someone inserts
        // a new enum member in the middle.
        builder.Property(deal => deal.Type)
            .HasColumnName("type")
            .HasConversion(type => SnakeCase(type.ToString()), value => Parse<DealType>(value))
            .IsRequired();

        builder.Property(deal => deal.Status)
            .HasColumnName("status")
            .HasConversion(status => SnakeCase(status.ToString()), value => Parse<DealStatus>(value))
            .IsRequired();

        builder.Property(deal => deal.VehicleId).HasColumnName("vehicle_id");
        builder.Property(deal => deal.CustomerId).HasColumnName("customer_id");
        builder.Property(deal => deal.JurisdictionId).HasColumnName("jurisdiction_id");
        builder.Property(deal => deal.SalespersonId).HasColumnName("salesperson_id");
        builder.Property(deal => deal.CurrentCalculationId).HasColumnName("current_calculation_id");
        builder.Property(deal => deal.QuotedAt).HasColumnName("quoted_at");
        builder.Property(deal => deal.ApprovedAt).HasColumnName("approved_at");
        builder.Property(deal => deal.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(deal => deal.CancelReason).HasColumnName("cancel_reason");
        builder.Property(deal => deal.Notes).HasColumnName("notes");

        builder.Property(deal => deal.CreatedAt).HasColumnName("created_at");
        builder.Property(deal => deal.CreatedBy).HasColumnName("created_by");
        builder.Property(deal => deal.UpdatedAt).HasColumnName("updated_at");
        builder.Property(deal => deal.UpdatedBy).HasColumnName("updated_by");
        builder.Property(deal => deal.DeletedAt).HasColumnName("deleted_at");
        builder.Property(deal => deal.DeletedBy).HasColumnName("deleted_by");

        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.Ignore(deal => deal.DomainEvents);

        // Declared so EF orders the writes correctly, not because anything
        // navigates it — hence WithMany() with no navigation property.
        //
        // Without this, saving a deal and its new calculation in one unit of work
        // emits the UPDATE that sets current_calculation_id BEFORE the INSERT of
        // the row it points at, and deal_current_calculation_fk rejects it. EF
        // cannot infer the dependency from a bare Guid? column; it has to be told.
        builder.HasOne<DealCalculationRecord>()
            .WithMany()
            .HasForeignKey(deal => deal.CurrentCalculationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Convenience, not a security control. RLS is what actually enforces this.
        builder.HasQueryFilter(deal => deal.DeletedAt == null);
    }

    internal static string SnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);

        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLower(value[index], CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    internal static T Parse<T>(string value)
        where T : struct, Enum =>
        Enum.Parse<T>(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);
}

/// <summary>
/// Maps <see cref="DealCalculationRecord"/> onto <c>sales.deal_calculation</c>.
/// </summary>
/// <remarks>
/// The table blocks <c>UPDATE</c> and <c>DELETE</c> by trigger, and the
/// application role is granted neither. Nothing in this mapping tries to work
/// around that: supersession is done by the store in explicit SQL, against the
/// two columns the trigger permits.
/// </remarks>
public sealed class DealCalculationConfiguration : IEntityTypeConfiguration<DealCalculationRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DealCalculationRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deal_calculation", "sales");
        builder.HasKey(record => record.Id);

        builder.Property(record => record.Id).HasColumnName("id");
        builder.Property(record => record.TenantId).HasColumnName("tenant_id");
        builder.Property(record => record.DealId).HasColumnName("deal_id");
        builder.Property(record => record.Version).HasColumnName("version");
        builder.Property(record => record.RuleSetId).HasColumnName("rule_set_id");
        builder.Property(record => record.RuleSetVersion).HasColumnName("rule_set_version");
        builder.Property(record => record.EngineVersion).HasColumnName("engine_version");
        builder.Property(record => record.JurisdictionCode).HasColumnName("jurisdiction_code");
        builder.Property(record => record.RegulatoryRulePackVersion)
            .HasColumnName("regulatory_rule_pack_version");
        builder.Property(record => record.LenderProgramVersionId)
            .HasColumnName("lender_program_version_id");
        builder.Property(record => record.CalculatedAt).HasColumnName("calculated_at");
        builder.Property(record => record.CalculatedBy).HasColumnName("calculated_by");

        builder.Property(record => record.Input).HasColumnName("input").HasColumnType("jsonb");
        builder.Property(record => record.Output).HasColumnName("output").HasColumnType("jsonb");

        Numeric(builder, record => record.SellingPrice, "selling_price");
        Numeric(builder, record => record.TradeAllowance, "trade_allowance");
        Numeric(builder, record => record.TradePayoff, "trade_payoff");
        Numeric(builder, record => record.TaxableAmount, "taxable_amount");
        Numeric(builder, record => record.SalesTax, "sales_tax");
        Numeric(builder, record => record.TotalFees, "total_fees");
        Numeric(builder, record => record.TotalStateFees, "total_state_fees");
        Numeric(builder, record => record.Rebates, "rebates");
        Numeric(builder, record => record.CashDown, "cash_down");
        Numeric(builder, record => record.AmountFinanced, "amount_financed");
        Numeric(builder, record => record.TotalSalePrice, "total_sale_price");
        Numeric(builder, record => record.BalanceDue, "balance_due");

        builder.Property(record => record.VehicleCost)
            .HasColumnName("vehicle_cost").HasColumnType("numeric(14,2)");
        builder.Property(record => record.FrontGross)
            .HasColumnName("front_gross").HasColumnType("numeric(14,2)");
        builder.Property(record => record.BackGross)
            .HasColumnName("back_gross").HasColumnType("numeric(14,2)");
        builder.Property(record => record.TotalGross)
            .HasColumnName("total_gross").HasColumnType("numeric(14,2)");

        builder.Property(record => record.ContentHash).HasColumnName("content_hash");
        builder.Property(record => record.SupersededAt).HasColumnName("superseded_at");
        builder.Property(record => record.SupersededReason).HasColumnName("superseded_reason");
    }

    private static void Numeric(
        EntityTypeBuilder<DealCalculationRecord> builder,
        System.Linq.Expressions.Expression<Func<DealCalculationRecord, decimal>> property,
        string column) =>
        builder.Property(property).HasColumnName(column).HasColumnType("numeric(14,2)");
}
