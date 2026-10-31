using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Application;

/// <summary>One line on the deal, as it arrives on the wire.</summary>
/// <param name="Kind">fee, product, accessory, discount, rebate, service_contract, gap, warranty or tax_override.</param>
/// <param name="Code">A stable code, matching a fee definition where one applies.</param>
/// <param name="Description">What appears on the contract.</param>
/// <param name="Amount">A decimal string, always positive.</param>
/// <param name="Cost">Dealer cost, where known.</param>
/// <param name="IsTaxable">Whether this line enters the taxable base, if the rule set does not say.</param>
/// <param name="IsStateFee">A pass-through to the state.</param>
/// <param name="IsConditional">An incentive the customer may not qualify for.</param>
public sealed record DealLineItemRequest(
    string Kind,
    string? Code,
    string Description,
    string Amount,
    string? Cost = null,
    bool IsTaxable = false,
    bool IsStateFee = false,
    bool IsConditional = false);

/// <summary>The trade, as it arrives on the wire.</summary>
/// <param name="Allowance">What the customer is credited.</param>
/// <param name="Payoff">What is owed on it.</param>
/// <param name="ActualCashValue">What the dealer thinks it is worth.</param>
public sealed record TradeInRequest(string Allowance, string Payoff, string? ActualCashValue = null);

/// <summary>The financing, as it arrives on the wire.</summary>
/// <param name="Apr">A decimal string in percent: <c>8.99</c> means 8.99%.</param>
/// <param name="TermMonths">Term in months.</param>
/// <param name="Frequency">monthly, semimonthly, biweekly or weekly.</param>
/// <param name="Method">standard_amortization, simple_interest or precomputed.</param>
/// <param name="BuyRate">The lender's rate, where the dealer is participating.</param>
/// <param name="ReserveAmount">Dealer participation.</param>
public sealed record FinanceRequestDto(
    string Apr,
    int TermMonths,
    string Frequency = "monthly",
    string Method = "standard_amortization",
    string? BuyRate = null,
    string? ReserveAmount = null);

/// <summary>
/// What to price.
/// </summary>
/// <remarks>
/// <para>
/// Every monetary value is a <b>string</b>. A JSON number is parsed by a browser
/// into an IEEE-754 double, and a price that round-trips through one can be wrong
/// by a cent — on a figure that is printed on a contract a consumer signs.
/// </para>
/// <para>
/// <see cref="DealDate"/> is optional and defaults to today, but it is accepted
/// because backdating a deal to the day it was actually written is a real thing a
/// dealer does, and the date decides which rule set applies.
/// </para>
/// </remarks>
public sealed record CalculateDealRequest
{
    /// <summary>
    /// The negotiated selling price, before accessories and discounts.
    /// </summary>
    /// <remarks>
    /// Nullable rather than <c>required</c>, deliberately. A <c>required</c> member
    /// makes the JSON deserializer throw when it is absent, which surfaces as an
    /// unhandled exception and a 500 — with the security headers stripped, because
    /// the exception handler clears the response. Validating it in
    /// <see cref="ToInput"/> instead produces a 422 that names the field, which is
    /// what a desking screen with thirty numbers on it actually needs.
    /// </remarks>
    public string? VehiclePrice { get; init; }

    /// <summary>Dealer cost, where the caller is entitled to supply it.</summary>
    public string? VehicleCost { get; init; }

    /// <summary>Fees, products, accessories, discounts and rebates.</summary>
    public IReadOnlyList<DealLineItemRequest> LineItems { get; init; } = [];

    /// <summary>The trade, where there is one.</summary>
    public TradeInRequest? Trade { get; init; }

    /// <summary>Cash down.</summary>
    public string? CashDown { get; init; }

    /// <summary>The financing, or null for a cash deal.</summary>
    public FinanceRequestDto? Finance { get; init; }

    /// <summary>The date the deal was written. Defaults to today.</summary>
    public DateOnly? DealDate { get; init; }

    /// <summary>
    /// Converts the wire shape into the engine's input.
    /// </summary>
    /// <remarks>
    /// Every parse failure is reported with the field that caused it, because
    /// "invalid request" on a desking screen with thirty numbers on it is not
    /// something anyone can act on.
    /// </remarks>
    public Result<DealInput> ToInput(Guid dealId, DateOnly today)
    {
        var price = Money.TryParse(VehiclePrice);
        if (price.IsFailure)
        {
            return Field(price.Error!, "vehiclePrice");
        }

        Money? cost = null;
        if (VehicleCost is not null)
        {
            var parsed = Money.TryParse(VehicleCost);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, "vehicleCost");
            }

            cost = parsed.Value;
        }

        var cashDown = Money.Zero;
        if (CashDown is not null)
        {
            var parsed = Money.TryParse(CashDown);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, "cashDown");
            }

            cashDown = parsed.Value;
        }

        var lines = new List<DealLineItem>();

        for (var index = 0; index < LineItems.Count; index++)
        {
            var converted = ConvertLine(LineItems[index], index);
            if (converted.IsFailure)
            {
                return converted.Error!;
            }

            lines.Add(converted.Value!);
        }

        TradeIn? trade = null;
        if (Trade is not null)
        {
            var converted = ConvertTrade(Trade);
            if (converted.IsFailure)
            {
                return converted.Error!;
            }

            trade = converted.Value;
        }

        FinanceRequest? finance = null;
        if (Finance is not null)
        {
            var converted = ConvertFinance(Finance);
            if (converted.IsFailure)
            {
                return converted.Error!;
            }

            finance = converted.Value;
        }

        return new DealInput
        {
            DealId = dealId,
            DealDate = DealDate ?? today,
            VehiclePrice = price.Value,
            VehicleCost = cost,
            LineItems = lines,
            Trade = trade,
            CashDown = cashDown,
            Finance = finance,
        };
    }

    private static Result<DealLineItem> ConvertLine(DealLineItemRequest line, int index)
    {
        if (!TryParseKind(line.Kind, out var kind))
        {
            return Error.Validation(
                "deal.invalid_line_kind",
                $"'{line.Kind}' is not a line item kind.",
                $"lineItems[{index}].kind");
        }

        var amount = Money.TryParse(line.Amount);
        if (amount.IsFailure)
        {
            return Field(amount.Error!, $"lineItems[{index}].amount");
        }

        Money? cost = null;
        if (line.Cost is not null)
        {
            var parsed = Money.TryParse(line.Cost);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, $"lineItems[{index}].cost");
            }

            cost = parsed.Value;
        }

        return new DealLineItem(
            kind, line.Code, line.Description, amount.Value, cost,
            line.IsTaxable, line.IsStateFee, line.IsConditional);
    }

    private static Result<TradeIn> ConvertTrade(TradeInRequest trade)
    {
        var allowance = Money.TryParse(trade.Allowance);
        if (allowance.IsFailure)
        {
            return Field(allowance.Error!, "trade.allowance");
        }

        var payoff = Money.TryParse(trade.Payoff);
        if (payoff.IsFailure)
        {
            return Field(payoff.Error!, "trade.payoff");
        }

        Money? acv = null;
        if (trade.ActualCashValue is not null)
        {
            var parsed = Money.TryParse(trade.ActualCashValue);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, "trade.actualCashValue");
            }

            acv = parsed.Value;
        }

        return new TradeIn(allowance.Value, payoff.Value, acv);
    }

    private static Result<FinanceRequest> ConvertFinance(FinanceRequestDto finance)
    {
        var apr = Percentage.TryParse(finance.Apr);
        if (apr.IsFailure)
        {
            return Field(apr.Error!, "finance.apr");
        }

        if (!TryParseFrequency(finance.Frequency, out var frequency))
        {
            return Error.Validation(
                "deal.invalid_frequency",
                $"'{finance.Frequency}' is not a payment frequency.",
                "finance.frequency");
        }

        if (!TryParseMethod(finance.Method, out var method))
        {
            return Error.Validation(
                "deal.invalid_payment_method",
                $"'{finance.Method}' is not a payment method.",
                "finance.method");
        }

        Percentage? buyRate = null;
        if (finance.BuyRate is not null)
        {
            var parsed = Percentage.TryParse(finance.BuyRate);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, "finance.buyRate");
            }

            buyRate = parsed.Value;
        }

        Money? reserve = null;
        if (finance.ReserveAmount is not null)
        {
            var parsed = Money.TryParse(finance.ReserveAmount);
            if (parsed.IsFailure)
            {
                return Field(parsed.Error!, "finance.reserveAmount");
            }

            reserve = parsed.Value;
        }

        return new FinanceRequest(apr.Value, finance.TermMonths, frequency, method, buyRate, reserve);
    }

    private static Error Field(Error error, string field) =>
        new(error.Code, error.Message, error.Kind, field);

    private static bool TryParseKind(string? value, out LineItemKind kind) =>
        Enum.TryParse(Denormalize(value), ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static bool TryParseFrequency(string? value, out PaymentFrequency frequency) =>
        Enum.TryParse(Denormalize(value), ignoreCase: true, out frequency) && Enum.IsDefined(frequency);

    private static bool TryParseMethod(string? value, out PaymentMethodKind method) =>
        Enum.TryParse(Denormalize(value), ignoreCase: true, out method) && Enum.IsDefined(method);

    /// <summary>Turns <c>service_contract</c> into <c>ServiceContract</c>.</summary>
    /// <remarks>
    /// The wire and the database both speak snake_case; the enum speaks PascalCase.
    /// Converting here keeps that translation in one place rather than in every
    /// caller.
    /// </remarks>
    private static string Denormalize(string? value) =>
        value is null ? string.Empty : value.Replace("_", string.Empty, StringComparison.Ordinal);
}
