using System.Globalization;
using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;

namespace MautoDesk.Sales.Application;

/// <summary>
/// Turns a snapshot into the shape the API returns.
/// </summary>
/// <remarks>
/// <para>
/// Every amount becomes a decimal string here, once. Doing it in one place is
/// what keeps a JSON number from appearing on the wire the day somebody adds a
/// field in a hurry.
/// </para>
/// <para>
/// Gross is omitted rather than zeroed when the caller cannot see it. Zero is a
/// number a manager might act on; absent says "you are not being shown this",
/// which is the truth.
/// </para>
/// </remarks>
public static class DealCalculationMapper
{
    /// <summary>Maps a stored snapshot and its deserialized output.</summary>
    /// <param name="record">The persisted row.</param>
    /// <param name="calculation">What the engine returned, from the stored output.</param>
    /// <param name="includeGross">Whether the caller holds <c>sales.gross.read</c>.</param>
    public static DealCalculationDto ToDto(
        DealCalculationRecord record,
        DealCalculation calculation,
        bool includeGross)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(calculation);

        return new DealCalculationDto(
            record.Id,
            record.DealId,
            record.Version,
            record.EngineVersion,
            record.RuleSetId,
            record.RuleSetVersion,
            record.CalculatedAt,
            calculation.SellingPrice.ToString(),
            calculation.TradeAllowance.ToString(),
            calculation.TradePayoff.ToString(),
            calculation.TradeEquity.ToString(),
            calculation.TaxableAmount.ToString(),
            calculation.TaxRate.ToString(),
            calculation.SalesTax.ToString(),
            calculation.TotalFees.ToString(),
            calculation.TotalStateFees.ToString(),
            calculation.TotalProducts.ToString(),
            calculation.Rebates.ToString(),
            calculation.CashDown.ToString(),
            calculation.TotalSalePrice.ToString(),
            calculation.BalanceDue.ToString(),
            calculation.AmountFinanced.ToString(),
            Financing(calculation),
            includeGross ? Gross(calculation) : null,
            calculation.LoanToValue?.ToString(),
            [.. calculation.Steps.Select(Step)],
            calculation.Advisories,
            Convert.ToHexString(record.ContentHash));
    }

    private static CalculationStepDto Step(CalculationStep step) => new(
        step.Order,
        step.Code,
        step.Label,
        step.Amount.ToString(),
        step.RunningTotal?.ToString(),
        step.Note);

    private static DealFinancingDto? Financing(DealCalculation calculation)
    {
        if (calculation.Payment is not { } payment
            || calculation.Apr is not { } apr
            || calculation.TermMonths is not { } term)
        {
            return null;
        }

        return new DealFinancingDto(
            apr.ToString(),
            term,
            payment.ToString(),
            calculation.PaymentCount ?? 0,
            calculation.FinanceCharge?.ToString() ?? "0.00",
            calculation.TotalOfPayments?.ToString() ?? "0.00",
            SnakeCase(calculation.PaymentMethod?.ToString() ?? string.Empty));
    }

    private static DealGrossDto? Gross(DealCalculation calculation)
    {
        if (calculation.TotalGross is not { } total)
        {
            return null;
        }

        return new DealGrossDto(
            calculation.VehicleCost?.ToString(),
            calculation.FrontGross?.ToString() ?? "0.00",
            calculation.BackGross?.ToString() ?? "0.00",
            total.ToString());
    }

    /// <summary>Turns <c>StandardAmortization</c> into <c>standard_amortization</c>.</summary>
    private static string SnakeCase(string value)
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
}
