using System.Data;
using MautoDesk.Compliance.Application;
using MautoDesk.Compliance.Domain;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Sales.Application;
using MautoDesk.Sales.Contracts;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Api;

/// <summary>
/// Assembles the facts a deal is judged on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This lives in the host on purpose.</b> The facts come from more than one
/// module — the deal and its figures from Sales, and in due course documents,
/// titles and lender decisions from theirs. Compliance must not take a reference
/// on any of them, and none of them may reach into Compliance. The host is the
/// only layer entitled to compose across modules, so the composition lives here
/// and each module stays independently extractable (ADR-0001).
/// </para>
/// <para>
/// <b>Every fact is read from the database.</b> Nothing here comes from a request
/// body. A rule that can be satisfied by a field the client sent is not a
/// compliance control, it is a suggestion — and the whole point of the regulatory
/// engine is that a dealer cannot talk it out of a blocking rule.
/// </para>
/// </remarks>
public sealed class DealFactBuilder : IRegulatoryContextBuilder
{
    private readonly MautoDeskDbContext _db;
    private readonly ICalculationStore _calculations;
    private readonly IClock _clock;

    /// <summary>Creates the builder.</summary>
    public DealFactBuilder(MautoDeskDbContext db, ICalculationStore calculations, IClock clock)
    {
        _db = db;
        _calculations = calculations;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result<DealFacts>> BuildAsync(Guid dealId, CancellationToken cancellationToken)
    {
        var deal = await ReadDealAsync(dealId, cancellationToken).ConfigureAwait(false);

        if (deal is null)
        {
            return Error.NotFound("deal.not_found", "That deal does not exist.");
        }

        if (deal.StateCode is null)
        {
            return Error.Validation(
                "deal.no_jurisdiction",
                "This deal has no jurisdiction, so there is no body of law to evaluate it against. " +
                "Set one before running compliance.");
        }

        // "OK" becomes "US-OK". The taxing jurisdiction carries a two-letter state
        // code; the regulatory jurisdiction is a law-making body identified the way
        // the DealerOS specification identifies one. Two different ideas that
        // happen to overlap in a single state, which is exactly why they are
        // separate tables.
        var jurisdictionCode = $"US-{deal.StateCode}";

        var builder = RegulatoryContext.Create()
            .With("deal.id", deal.Id.ToString())
            .With("deal.number", deal.DealNumber)
            .With("deal.type", deal.Type)
            .With("deal.status", deal.Status)
            .With("deal.hasVehicle", deal.VehicleId is not null)
            .With("deal.hasCustomer", deal.CustomerId is not null)
            .With("deal.stateCode", deal.StateCode);

        var dealDate = deal.QuotedAt is { } quoted
            ? DateOnly.FromDateTime(quoted.UtcDateTime)
            : _clock.Today;

        builder.With("deal.date", dealDate);

        var calculation = await _calculations
            .GetCurrentAsync(dealId, cancellationToken)
            .ConfigureAwait(false);

        if (calculation is null)
        {
            // An unpriced deal is a real state, not an error. Rules that depend on
            // money simply will not fire, and rules that do not — an odometer
            // disclosure, say — still apply. Saying "not priced" is more useful
            // than refusing to evaluate.
            builder.With("deal.isPriced", false);
        }
        else
        {
            var output = System.Text.Json.JsonSerializer
                .Deserialize<MautoDesk.Sales.Domain.DealCalculation>(
                    calculation.Output, SnapshotSerialization.Options);

            builder
                .With("deal.isPriced", true)
                .With("deal.sellingPrice", calculation.SellingPrice)
                .With("deal.totalSalePrice", calculation.TotalSalePrice)
                .With("deal.amountFinanced", calculation.AmountFinanced)
                .With("deal.cashDown", calculation.CashDown)
                .With("deal.salesTax", calculation.SalesTax)
                .With("deal.totalFees", calculation.TotalFees)
                .With("deal.tradeAllowance", calculation.TradeAllowance)
                .With("deal.tradePayoff", calculation.TradePayoff)
                .With("deal.hasTrade", calculation.TradeAllowance > 0m || calculation.TradePayoff > 0m)
                .With("deal.isFinanced", calculation.AmountFinanced > 0m)
                .With("deal.hasNegativeEquity", calculation.TradePayoff > calculation.TradeAllowance);

            if (output is not null)
            {
                builder
                    .With("deal.apr", output.Apr?.Percent)
                    .With("deal.termMonths", output.TermMonths)
                    .With("deal.payment", output.Payment?.Amount)
                    .With("deal.loanToValue", output.LoanToValue?.Percent)
                    .With("deal.hasConditionalRebate", !output.ConditionalRebates.IsZero);

                // Individual fees, so a rule can say "the doc fee is above its cap"
                // without the engine needing to know what a doc fee is.
                foreach (var line in output.Steps.Where(step => step.Code.StartsWith("fee.", StringComparison.Ordinal)))
                {
                    builder.With($"deal.{line.Code}", line.Amount.Amount);
                }
            }
        }

        var satisfied = await ReadSatisfiedRequirementsAsync(dealId, cancellationToken)
            .ConfigureAwait(false);

        builder.Satisfying(satisfied);

        return new DealFacts(builder.Build(), jurisdictionCode, dealDate);
    }

    /// <summary>
    /// The requirements this deal has already met.
    /// </summary>
    /// <remarks>
    /// Read from <c>compliance.document_requirement</c>, which is written by the
    /// document engine as paperwork is produced and signed. Until that module
    /// exists the table is empty, so every requirement reads as outstanding —
    /// which is the correct answer, not a gap: no document has in fact been
    /// produced.
    /// </remarks>
    private async Task<List<string>> ReadSatisfiedRequirementsAsync(
        Guid dealId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            select code from compliance.document_requirement
             where deal_id = @deal_id and status in ('signed','waived','not_applicable')
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("deal_id", NpgsqlDbType.Uuid) { Value = dealId });

        var codes = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private async Task<DealRow?> ReadDealAsync(Guid dealId, CancellationToken cancellationToken)
    {
        const string Sql = """
            select d.id, d.deal_number, d.type, d.status, d.vehicle_id, d.customer_id,
                   d.quoted_at, j.state_code
              from sales.deal d
              left join sales.tax_jurisdiction j on j.id = d.jurisdiction_id
             where d.id = @deal_id and d.deleted_at is null
            """;

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("deal_id", NpgsqlDbType.Uuid) { Value = dealId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DealRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetGuid(4),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetGuid(5),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(6),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(7).Trim());
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            // Through EF so the tenant interceptor runs; every read here is subject
            // to row-level security on the same connection.
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private sealed record DealRow(
        Guid Id,
        string DealNumber,
        string Type,
        string Status,
        Guid? VehicleId,
        Guid? CustomerId,
        DateTimeOffset? QuotedAt,
        string? StateCode);
}
