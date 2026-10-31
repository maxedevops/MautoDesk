using System.Globalization;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Sales.Application;
using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Sales.Infrastructure;

/// <summary>EF-backed persistence for the deal aggregate.</summary>
/// <remarks>
/// No <c>WHERE tenant_id = ...</c> anywhere, and that is not an oversight: the
/// connection carries <c>app.tenant_id</c> and row-level security filters
/// underneath. An application predicate here would invite the belief that the
/// predicate is what protects us, and the day someone writes a query without it,
/// that belief becomes a breach (ADR-0002).
/// </remarks>
public sealed class DealRepository : IDealRepository
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the repository.</summary>
    public DealRepository(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public Task<Deal?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<Deal>().FirstOrDefaultAsync(deal => deal.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DealNumberExistsAsync(string dealNumber, CancellationToken cancellationToken) =>
        _db.Set<Deal>().AnyAsync(deal => deal.DealNumber == dealNumber, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Counts existing deals and adds one. Adequate for a dealership's volume and
    /// deliberately simple; the unique index on <c>(tenant_id, deal_number)</c> is
    /// what actually prevents a collision, and the handler reports it as a conflict
    /// rather than this method pretending to be a sequence generator.
    /// </remarks>
    public async Task<string> NextDealNumberAsync(CancellationToken cancellationToken)
    {
        var count = await _db.Set<Deal>().CountAsync(cancellationToken).ConfigureAwait(false);

        return string.Create(CultureInfo.InvariantCulture, $"D-{count + 1:D5}");
    }

    /// <inheritdoc />
    public void Add(Deal deal) => _db.Set<Deal>().Add(deal);
}

/// <summary>Commits the unit of work, outbox included.</summary>
public sealed class SalesUnitOfWork : IUnitOfWork
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the unit of work.</summary>
    public SalesUnitOfWork(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}

/// <summary>The append-only calculation ledger.</summary>
public sealed class CalculationStore : ICalculationStore
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the store.</summary>
    public CalculationStore(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<int> NextVersionAsync(Guid dealId, CancellationToken cancellationToken)
    {
        var highest = await _db.Set<DealCalculationRecord>()
            .Where(record => record.DealId == dealId)
            .MaxAsync(record => (int?)record.Version, cancellationToken)
            .ConfigureAwait(false);

        return (highest ?? 0) + 1;
    }

    /// <inheritdoc />
    public void Add(DealCalculationRecord record) => _db.Set<DealCalculationRecord>().Add(record);

    /// <inheritdoc />
    public Task<DealCalculationRecord?> GetAsync(Guid calculationId, CancellationToken cancellationToken) =>
        _db.Set<DealCalculationRecord>()
            .FirstOrDefaultAsync(record => record.Id == calculationId, cancellationToken);

    /// <inheritdoc />
    public Task<DealCalculationRecord?> GetCurrentAsync(Guid dealId, CancellationToken cancellationToken) =>
        _db.Set<DealCalculationRecord>()
            .Where(record => record.DealId == dealId)
            .OrderByDescending(record => record.Version)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Reads tax rule sets.</summary>
public sealed class TaxRuleSetStore : ITaxRuleSetStore
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the store.</summary>
    public TaxRuleSetStore(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    /// <remarks>
    /// <c>sales.rule_set</c> is shared reference data — published law, identical
    /// for every tenant — so it is on the RLS exemption list and read-only to the
    /// application role. Unapproved rows come back too; see the port for why.
    /// </remarks>
    public async Task<TaxRuleSetRow?> FindAsync(
        Guid jurisdictionId,
        DateOnly onDate,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select id, jurisdiction_id, version, effective_from, effective_to,
                   approved_at is not null as is_approved, source_citation, rules::text
              from sales.rule_set
             where jurisdiction_id = @jurisdiction_id
               and effective_from <= @on_date
               and (effective_to is null or effective_to > @on_date)
             order by approved_at is not null desc, version desc
             limit 1
            """;

        var connection = _db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("jurisdiction_id", NpgsqlDbType.Uuid)
        {
            Value = jurisdictionId,
        });
        command.Parameters.Add(new NpgsqlParameter("on_date", NpgsqlDbType.Date) { Value = onDate });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TaxRuleSetRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            DateOnly.FromDateTime(reader.GetDateTime(3)),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(4)),
            reader.GetBoolean(5),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? string.Empty
                : reader.GetString(6),
            reader.GetString(7));
    }
}

/// <summary>Projects deals straight to DTOs.</summary>
public sealed class DealReadStore : IDealReadStore
{
    private readonly MautoDeskDbContext _db;

    /// <summary>Creates the read store.</summary>
    public DealReadStore(MautoDeskDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<DealDto?> GetAsync(
        Guid dealId,
        bool includeGross,
        CancellationToken cancellationToken)
    {
        var deal = await _db.Set<Deal>()
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == dealId, cancellationToken)
            .ConfigureAwait(false);

        if (deal is null)
        {
            return null;
        }

        DealCalculationDto? current = null;

        if (deal.CurrentCalculationId is { } calculationId)
        {
            var record = await _db.Set<DealCalculationRecord>()
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.Id == calculationId, cancellationToken)
                .ConfigureAwait(false);

            if (record is not null)
            {
                var output = System.Text.Json.JsonSerializer.Deserialize<DealCalculation>(
                    record.Output, SnapshotSerialization.Options);

                if (output is not null)
                {
                    current = DealCalculationMapper.ToDto(record, output, includeGross);
                }
            }
        }

        return new DealDto(
            deal.Id,
            deal.DealNumber,
            DealConfiguration.SnakeCase(deal.Type.ToString()),
            DealConfiguration.SnakeCase(deal.Status.ToString()),
            deal.VehicleId,
            deal.CustomerId,
            deal.JurisdictionId,
            deal.SalespersonId,
            deal.CurrentCalculationId,
            deal.Notes,
            deal.QuotedAt,
            deal.ApprovedAt,
            deal.CancelledAt,
            deal.CancelReason,
            deal.CreatedAt,
            deal.UpdatedAt,
            current);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The list joins the current snapshot for its totals rather than reading a
    /// column on the deal, because there is no such column. That join is the price
    /// of having one source of truth for a number that reaches a contract, and it
    /// is served by <c>deal_calculation_deal_ix</c>.
    /// </remarks>
    public async Task<PagedResult<DealSummaryDto>> ListAsync(
        DealListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.Set<Deal>().AsNoTracking();

        if (filter.Statuses is { Count: > 0 })
        {
            var statuses = filter.Statuses
                .Select(status => DealConfiguration.Parse<DealStatus>(status))
                .ToList();

            query = query.Where(deal => statuses.Contains(deal.Status));
        }

        if (filter.CustomerId is { } customerId)
        {
            query = query.Where(deal => deal.CustomerId == customerId);
        }

        if (filter.VehicleId is { } vehicleId)
        {
            query = query.Where(deal => deal.VehicleId == vehicleId);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var deals = await query
            .OrderByDescending(deal => deal.UpdatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var calculationIds = deals
            .Select(deal => deal.CurrentCalculationId)
            .OfType<Guid>()
            .ToList();

        var calculations = await _db.Set<DealCalculationRecord>()
            .AsNoTracking()
            .Where(record => calculationIds.Contains(record.Id))
            .ToDictionaryAsync(record => record.Id, cancellationToken)
            .ConfigureAwait(false);

        var items = deals.Select(deal =>
        {
            DealCalculationRecord? record = null;

            if (deal.CurrentCalculationId is { } id)
            {
                calculations.TryGetValue(id, out record);
            }

            return new DealSummaryDto(
                deal.Id,
                deal.DealNumber,
                DealConfiguration.SnakeCase(deal.Type.ToString()),
                DealConfiguration.SnakeCase(deal.Status.ToString()),
                deal.VehicleId,
                deal.CustomerId,
                record?.TotalSalePrice.ToString("0.00", CultureInfo.InvariantCulture),
                record?.AmountFinanced.ToString("0.00", CultureInfo.InvariantCulture),
                PaymentOf(record),
                deal.CreatedAt,
                deal.UpdatedAt);
        }).ToList();

        return new PagedResult<DealSummaryDto>(items, filter.Page, filter.PageSize, total);
    }

    /// <summary>Reads the payment out of the stored output.</summary>
    /// <remarks>
    /// The payment is not a projected column on <c>deal_calculation</c>, so the
    /// list reads it from the snapshot it already loaded rather than adding a
    /// column that would become a second place for it to live.
    /// </remarks>
    private static string? PaymentOf(DealCalculationRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        var output = System.Text.Json.JsonSerializer.Deserialize<DealCalculation>(
            record.Output, SnapshotSerialization.Options);

        return output?.Payment?.ToString();
    }
}
