namespace MautoDesk.Sales.Contracts;

/// <summary>
/// The Sales module's public surface.
/// </summary>
/// <remarks>
/// The only project in the Sales module another module may reference. An
/// architecture test fails the build if anything reaches into
/// <c>Sales.Domain</c>, <c>.Application</c> or <c>.Infrastructure</c> from
/// outside — which is what stops the deal engine's internals from becoming
/// everyone's dependency (ADR-0001).
/// </remarks>
public static class SalesPermissions
{
    /// <summary>Read deals the user is on.</summary>
    public const string DealRead = "sales.deal.read";

    /// <summary>Read every deal in the store.</summary>
    public const string DealReadAll = "sales.deal.read.all";

    /// <summary>Create and change deals.</summary>
    public const string DealWrite = "sales.deal.write";

    /// <summary>Approve a deal for contracting.</summary>
    public const string DealApprove = "sales.deal.approve";

    /// <summary>Void a deal.</summary>
    public const string DealVoid = "sales.deal.void";

    /// <summary>Read financing terms, including buy rate and reserve.</summary>
    /// <remarks>
    /// Sensitive. Rate participation is regulated and disclosure-sensitive, so any
    /// screen exposing buy rate against sell rate is gated on this and audited.
    /// </remarks>
    public const string FinanceRead = "sales.deal.finance.read";

    /// <summary>Change financing terms.</summary>
    public const string FinanceWrite = "sales.deal.finance.write";

    /// <summary>See gross profit on a deal.</summary>
    public const string GrossRead = "sales.gross.read";

    /// <summary>Configure dealer fees.</summary>
    public const string FeeWrite = "sales.fee.write";
}

/// <summary>A deal in a list.</summary>
/// <remarks>
/// The money here comes from the current calculation snapshot, never from a
/// column on the deal — there is no such column, on purpose.
/// </remarks>
public sealed record DealSummaryDto(
    Guid Id,
    string DealNumber,
    string Type,
    string Status,
    Guid? VehicleId,
    Guid? CustomerId,
    string? TotalSalePrice,
    string? AmountFinanced,
    string? Payment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A deal and its current figures.</summary>
public sealed record DealDto(
    Guid Id,
    string DealNumber,
    string Type,
    string Status,
    Guid? VehicleId,
    Guid? CustomerId,
    Guid? JurisdictionId,
    Guid? SalespersonId,
    Guid? CurrentCalculationId,
    string? Notes,
    DateTimeOffset? QuotedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DealCalculationDto? CurrentCalculation);

/// <summary>One step of the calculation, as the desking screen shows it.</summary>
/// <remarks>Money is a decimal string, never a JSON number.</remarks>
public sealed record CalculationStepDto(
    int Order,
    string Code,
    string Label,
    string Amount,
    string? RunningTotal,
    string? Note);

/// <summary>
/// A calculation snapshot as other modules and the API see it.
/// </summary>
/// <remarks>
/// <para>
/// Every monetary field is a <b>string</b> holding a plain decimal. This is not
/// fussiness: a JSON number is parsed by every JavaScript client into an IEEE-754
/// double, which cannot represent 0.1 exactly. A price that round-trips through
/// one can be wrong by a cent, and this figure is printed on a retail contract.
/// </para>
/// <para>
/// <see cref="Gross"/> and the financing detail are null when the caller lacks
/// the permission to see them, rather than zero. Zero would be a lie a manager
/// might act on.
/// </para>
/// </remarks>
public sealed record DealCalculationDto(
    Guid Id,
    Guid DealId,
    int Version,
    string EngineVersion,
    Guid? RuleSetId,
    int? RuleSetVersion,
    DateTimeOffset CalculatedAt,
    string SellingPrice,
    string TradeAllowance,
    string TradePayoff,
    string TradeEquity,
    string TaxableAmount,
    string TaxRate,
    string SalesTax,
    string TotalFees,
    string TotalStateFees,
    string TotalProducts,
    string Rebates,
    string CashDown,
    string TotalSalePrice,
    string BalanceDue,
    string AmountFinanced,
    DealFinancingDto? Financing,
    DealGrossDto? Gross,
    string? LoanToValue,
    IReadOnlyList<CalculationStepDto> Steps,
    IReadOnlyList<string> Advisories,
    string ContentHash);

/// <summary>The financing figures, where the deal is financed.</summary>
public sealed record DealFinancingDto(
    string Apr,
    int TermMonths,
    string Payment,
    int PaymentCount,
    string FinanceCharge,
    string TotalOfPayments,
    string PaymentMethod);

/// <summary>Gross profit, gated behind <see cref="SalesPermissions.GrossRead"/>.</summary>
public sealed record DealGrossDto(
    string? VehicleCost,
    string FrontGross,
    string BackGross,
    string TotalGross);
