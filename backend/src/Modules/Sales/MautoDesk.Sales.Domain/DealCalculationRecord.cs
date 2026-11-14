using System.Security.Cryptography;
using System.Text;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>
/// A persisted calculation snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>sales.deal_calculation</c>, which blocks <c>UPDATE</c> and
/// <c>DELETE</c> by trigger and revokes both from the application role. A
/// correction is a new version with the previous row marked superseded — never
/// an edit — because an edit would silently restate a number that may already be
/// on a signed contract.
/// </para>
/// <para>
/// Deliberately not an <see cref="AggregateRoot"/>. An aggregate has behaviour
/// and invariants to protect over time; this is a fact that happened, written
/// once and never touched again. Giving it mutating methods would suggest
/// otherwise.
/// </para>
/// </remarks>
public sealed class DealCalculationRecord
{
    /// <summary>Required by EF Core materialization.</summary>
    private DealCalculationRecord()
    {
    }

    /// <summary>The snapshot row.</summary>
    public Guid Id { get; private set; }

    /// <summary>The dealership.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The deal.</summary>
    public Guid DealId { get; private set; }

    /// <summary>Snapshot version, 1-based and dense within a deal.</summary>
    public int Version { get; private set; }

    /// <summary>The tax rule set used.</summary>
    public Guid? RuleSetId { get; private set; }

    /// <summary>That rule set's version.</summary>
    public int? RuleSetVersion { get; private set; }

    /// <summary>The engine that produced this.</summary>
    public string EngineVersion { get; private set; } = string.Empty;

    /// <summary>The regulatory jurisdiction, distinct from the taxing one.</summary>
    public string? JurisdictionCode { get; private set; }

    /// <summary>The compliance pack in force, where one was resolved.</summary>
    public string? RegulatoryRulePackVersion { get; private set; }

    /// <summary>The lender program version pinned to this quote, if financed.</summary>
    public Guid? LenderProgramVersionId { get; private set; }

    /// <summary>When it was computed.</summary>
    public DateTimeOffset CalculatedAt { get; private set; }

    /// <summary>Who asked for it.</summary>
    public Guid? CalculatedBy { get; private set; }

    /// <summary>The exact serialized <see cref="DealInput"/> the engine saw.</summary>
    public string Input { get; private set; } = "{}";

    /// <summary>The exact serialized <see cref="DealCalculation"/> it returned.</summary>
    public string Output { get; private set; } = "{}";

    /// <summary>Selling price after accessories and discounts.</summary>
    public decimal SellingPrice { get; private set; }

    /// <summary>Trade allowance.</summary>
    public decimal TradeAllowance { get; private set; }

    /// <summary>Trade payoff.</summary>
    public decimal TradePayoff { get; private set; }

    /// <summary>The base the tax rate was applied to.</summary>
    public decimal TaxableAmount { get; private set; }

    /// <summary>The sales tax.</summary>
    public decimal SalesTax { get; private set; }

    /// <summary>All fees.</summary>
    public decimal TotalFees { get; private set; }

    /// <summary>The portion of fees passed through to the state.</summary>
    public decimal TotalStateFees { get; private set; }

    /// <summary>Rebates and incentives.</summary>
    public decimal Rebates { get; private set; }

    /// <summary>Cash down.</summary>
    public decimal CashDown { get; private set; }

    /// <summary>The amount financed.</summary>
    public decimal AmountFinanced { get; private set; }

    /// <summary>Price plus tax, fees and products.</summary>
    public decimal TotalSalePrice { get; private set; }

    /// <summary>What is left after credits.</summary>
    public decimal BalanceDue { get; private set; }

    /// <summary>Vehicle cost, frozen at snapshot time.</summary>
    public decimal? VehicleCost { get; private set; }

    /// <summary>Gross on the vehicle.</summary>
    public decimal? FrontGross { get; private set; }

    /// <summary>Gross on products, dealer fees and reserve.</summary>
    public decimal? BackGross { get; private set; }

    /// <summary>Front plus back.</summary>
    public decimal? TotalGross { get; private set; }

    /// <summary>SHA-256 of input and output. Printed on the contract.</summary>
    public byte[] ContentHash { get; private set; } = [];

    /// <summary>Set when a later version replaced this one.</summary>
    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>Why it was replaced.</summary>
    public string? SupersededReason { get; private set; }

    /// <summary>
    /// Builds a row from what the engine produced.
    /// </summary>
    /// <param name="tenantId">The dealership.</param>
    /// <param name="dealId">The deal.</param>
    /// <param name="version">The next version number for this deal.</param>
    /// <param name="calculation">What the engine returned.</param>
    /// <param name="inputJson">The serialized input the engine saw, verbatim.</param>
    /// <param name="outputJson">The serialized output it returned, verbatim.</param>
    /// <param name="now">The clock.</param>
    /// <param name="userId">Who asked.</param>
    /// <param name="jurisdictionCode">The regulatory jurisdiction, where known.</param>
    /// <param name="rulePackVersion">The compliance pack, where one was resolved.</param>
    public static DealCalculationRecord From(
        Guid tenantId,
        Guid dealId,
        int version,
        DealCalculation calculation,
        string inputJson,
        string outputJson,
        DateTimeOffset now,
        Guid? userId,
        string? jurisdictionCode = null,
        string? rulePackVersion = null)
    {
        ArgumentNullException.ThrowIfNull(calculation);

        return new DealCalculationRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DealId = dealId,
            Version = version,
            RuleSetId = calculation.RuleSetId,
            RuleSetVersion = calculation.RuleSetVersion,
            EngineVersion = calculation.EngineVersion,
            JurisdictionCode = jurisdictionCode,
            RegulatoryRulePackVersion = rulePackVersion,
            CalculatedAt = now,
            CalculatedBy = userId,
            Input = inputJson,
            Output = outputJson,
            SellingPrice = calculation.SellingPrice.Amount,
            TradeAllowance = calculation.TradeAllowance.Amount,
            TradePayoff = calculation.TradePayoff.Amount,
            TaxableAmount = calculation.TaxableAmount.Amount,
            SalesTax = calculation.SalesTax.Amount,
            TotalFees = calculation.TotalFees.Amount,
            TotalStateFees = calculation.TotalStateFees.Amount,
            Rebates = calculation.Rebates.Amount,
            CashDown = calculation.CashDown.Amount,
            AmountFinanced = calculation.AmountFinanced.Amount,
            TotalSalePrice = calculation.TotalSalePrice.Amount,
            BalanceDue = calculation.BalanceDue.Amount,
            VehicleCost = calculation.VehicleCost?.Amount,
            FrontGross = calculation.FrontGross?.Amount,
            BackGross = calculation.BackGross?.Amount,
            TotalGross = calculation.TotalGross?.Amount,
            ContentHash = HashOf(inputJson, outputJson),
        };
    }

    /// <summary>
    /// Hashes the input and output together.
    /// </summary>
    /// <remarks>
    /// Over the serialized text rather than the object, so the hash covers exactly
    /// the bytes stored. A dispute is settled by rehashing what is in the database
    /// and comparing — which only works if the hash was taken of that and not of
    /// something equivalent to it.
    /// </remarks>
    public static byte[] HashOf(string inputJson, string outputJson) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(inputJson + "\n" + outputJson));
}
