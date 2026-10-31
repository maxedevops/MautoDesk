using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>
/// Turns a <see cref="DealInput"/> into a <see cref="DealCalculation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure.</b> No database, no HTTP, no clock, no configuration. Everything it
/// needs arrives as an argument, and the same arguments always produce the same
/// result. That is not a stylistic preference: it is what makes the twenty-five
/// step pipeline exhaustively testable without a container, and what makes a
/// disputed 2026 deal replayable in 2029 from nothing but the stored input and a
/// pinned engine version.
/// </para>
/// <para>
/// <b>It refuses more often than it computes, today.</b> Every seeded rule set is
/// unapproved, so <see cref="Calculate"/> returns an error for every jurisdiction
/// until a named reviewer approves one against a cited primary source. That is the
/// designed behaviour, asserted by a test, and it is not to be relaxed to make a
/// demo work.
/// </para>
/// <para>
/// <b>Rounding is explicit and happens at boundaries.</b> Line items arrive
/// already at the cent. Tax is computed against an unrounded rate and rounded
/// once. Payments are rounded once, and the finance charge is derived from the
/// rounded payment — because the rounded payment is what the customer actually
/// pays, so the rounding belongs inside the disclosed figures rather than beside
/// them.
/// </para>
/// </remarks>
public static class DealEngine
{
    /// <summary>
    /// The engine's version, stamped onto every snapshot it produces.
    /// </summary>
    /// <remarks>
    /// Bump this whenever a change alters a computed figure for inputs that
    /// previously produced a different one. A snapshot records the version that
    /// produced it, so a replay pins the arithmetic of the day rather than
    /// today's.
    /// </remarks>
    public const string Version = "1.0.0";

    /// <summary>Computes the deal, or explains why it cannot.</summary>
    /// <param name="input">Everything the calculation depends on.</param>
    /// <param name="rules">The approved rule set governing <see cref="DealInput.DealDate"/>.</param>
    public static Result<DealCalculation> Calculate(DealInput input, TaxRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);

        var refusal = Validate(input, rules);
        if (refusal is not null)
        {
            return refusal;
        }

        var steps = new List<CalculationStep>();
        var advisories = new List<string>();
        var order = 0;

        // -- 1. Vehicle price --------------------------------------------------
        var vehiclePrice = input.VehiclePrice;
        steps.Add(new CalculationStep(
            ++order, "vehicle.price", "Vehicle price", vehiclePrice, vehiclePrice));

        // -- 2. Dealer-installed accessories -----------------------------------
        var accessories = SumOf(input, LineItemKind.Accessory);
        var priceWithAccessories = vehiclePrice + accessories;
        if (!accessories.IsZero)
        {
            steps.Add(new CalculationStep(
                ++order, "vehicle.accessories", "Dealer-installed accessories",
                accessories, priceWithAccessories));
        }

        // -- 3. Dealer discount ------------------------------------------------
        var discounts = SumOf(input, LineItemKind.Discount);
        var sellingPrice = priceWithAccessories - discounts;
        if (!discounts.IsZero)
        {
            steps.Add(new CalculationStep(
                ++order, "vehicle.discount", "Dealer discount", -discounts, sellingPrice));
        }

        // -- 4 & 5. Rebates and conditional incentives --------------------------
        // Split because several states treat a conditional incentive differently
        // in advertising, and because a deal carried by a rebate the customer
        // turns out not to qualify for is a deal that unwinds at delivery.
        var rebateLines = input.LineItems.Where(line => line.Kind == LineItemKind.Rebate).ToList();
        var unconditionalRebates = Sum(rebateLines.Where(line => !line.IsConditional));
        var conditionalRebates = Sum(rebateLines.Where(line => line.IsConditional));
        var rebates = unconditionalRebates + conditionalRebates;

        if (!rebates.IsZero)
        {
            steps.Add(new CalculationStep(
                ++order, "rebates.total", "Rebates and incentives", rebates, null,
                conditionalRebates.IsZero
                    ? null
                    : $"{conditionalRebates} of this is conditional on customer eligibility."));
        }

        if (!conditionalRebates.IsZero)
        {
            advisories.Add(
                $"{conditionalRebates} of the rebates applied are conditional. Confirm the " +
                "customer qualifies before contracting.");
        }

        // -- 6, 7, 8. Trade allowance, payoff, equity ---------------------------
        var tradeAllowance = input.Trade?.Allowance ?? Money.Zero;
        var tradePayoff = input.Trade?.Payoff ?? Money.Zero;
        var tradeEquity = tradeAllowance - tradePayoff;

        if (input.Trade is not null)
        {
            steps.Add(new CalculationStep(
                ++order, "trade.allowance", "Trade allowance", tradeAllowance));
            steps.Add(new CalculationStep(
                ++order, "trade.payoff", "Trade payoff", -tradePayoff));
            steps.Add(new CalculationStep(
                ++order, "trade.equity", "Trade equity", tradeEquity, null,
                tradeEquity.IsNegative
                    ? "Negative equity is added to the amount financed."
                    : null));

            if (tradeEquity.IsNegative)
            {
                advisories.Add(
                    $"The trade carries {-tradeEquity} of negative equity, which is being " +
                    "financed on top of the vehicle.");
            }
        }

        // -- 9. Taxable amount --------------------------------------------------
        var taxable = TaxableAmount(
            sellingPrice, accessories, unconditionalRebates, conditionalRebates,
            tradeAllowance, input, rules, out var taxNote);

        steps.Add(new CalculationStep(
            ++order, "tax.basis", "Taxable amount", taxable, null, taxNote));

        // -- 10. Sales tax ------------------------------------------------------
        var salesTax = ComputeTax(taxable, rules, input, advisories);
        steps.Add(new CalculationStep(
            ++order, "tax.amount", $"Sales tax at {rules.CombinedRate}%", salesTax));

        // -- 11 to 14. Fees ------------------------------------------------------
        var (totalFees, stateFees) = SumFees(input, rules, steps, advisories, ref order);

        // -- 15. Products --------------------------------------------------------
        var products = Sum(input.LineItems.Where(IsProduct));
        if (!products.IsZero)
        {
            steps.Add(new CalculationStep(
                ++order, "products.total", "Aftermarket products", products));
        }

        // -- Total sale price ----------------------------------------------------
        var totalSalePrice = sellingPrice + salesTax + totalFees + products;
        steps.Add(new CalculationStep(
            ++order, "total.sale_price", "Total sale price", totalSalePrice, totalSalePrice));

        // -- 16. Cash down --------------------------------------------------------
        var cashDown = input.CashDown;
        if (!cashDown.IsZero)
        {
            steps.Add(new CalculationStep(++order, "down.cash", "Cash down", -cashDown));
        }

        // -- 17. Balance due / amount financed ------------------------------------
        // Trade EQUITY, not allowance: a payoff larger than the allowance rolls the
        // difference forward rather than reducing what is owed.
        var balanceDue = totalSalePrice - cashDown - rebates - tradeEquity;

        steps.Add(new CalculationStep(
            ++order, "total.balance_due", "Balance due", balanceDue, balanceDue,
            tradeEquity.IsNegative
                ? "Includes negative equity carried from the trade."
                : null));

        // -- 18 to 21. Financing ---------------------------------------------------
        var financed = input.Finance is not null;
        var amountFinanced = financed ? balanceDue : Money.Zero;
        PaymentSchedule? schedule = null;

        if (financed)
        {
            if (amountFinanced.IsNegative)
            {
                // The credits exceed the sale price. Financing a negative number is
                // meaningless, and quietly treating it as zero would hide a data
                // entry error behind a plausible-looking $0 payment.
                return Error.Validation(
                    "deal.negative_amount_financed",
                    $"The balance due is {balanceDue}. Cash down, rebates and trade equity " +
                    "exceed the total sale price, so there is nothing to finance.");
            }

            var method = PaymentMethods.For(input.Finance!.Method);
            schedule = method.Compute(
                amountFinanced, input.Finance.Apr, input.Finance.TermMonths, input.Finance.Frequency);

            steps.Add(new CalculationStep(
                ++order, "finance.amount", "Amount financed", amountFinanced, amountFinanced));
            steps.Add(new CalculationStep(
                ++order, "finance.payment",
                $"Payment, {schedule.PaymentCount} at {input.Finance.Apr}%",
                schedule.Payment, null,
                $"Computed with {input.Finance.Method}."));
            steps.Add(new CalculationStep(
                ++order, "finance.charge", "Finance charge", schedule.FinanceCharge));
            steps.Add(new CalculationStep(
                ++order, "finance.total", "Total of payments", schedule.TotalOfPayments));
        }

        // -- 22 & 23. Gross and reserve ---------------------------------------------
        var (frontGross, backGross, totalGross) = ComputeGross(
            input, sellingPrice, products, totalFees, stateFees, steps, ref order);

        // -- Loan to value ------------------------------------------------------------
        var loanToValue = ComputeLoanToValue(amountFinanced, sellingPrice, financed);

        if (loanToValue is not null && loanToValue.Value.Percent > 130m)
        {
            advisories.Add(
                $"Loan to value is {loanToValue.Value}%, which most lender programs cap " +
                "well below. Expect this structure to be declined.");
        }

        return new DealCalculation
        {
            EngineVersion = Version,
            RuleSetId = rules.RuleSetId,
            RuleSetVersion = rules.Version,
            SellingPrice = sellingPrice,
            Accessories = accessories,
            Discounts = discounts,
            Rebates = rebates,
            ConditionalRebates = conditionalRebates,
            TradeAllowance = tradeAllowance,
            TradePayoff = tradePayoff,
            TradeEquity = tradeEquity,
            TaxableAmount = taxable,
            TaxRate = rules.CombinedRate,
            SalesTax = salesTax,
            TotalFees = totalFees,
            TotalStateFees = stateFees,
            TotalProducts = products,
            CashDown = cashDown,
            TotalSalePrice = totalSalePrice,
            BalanceDue = balanceDue,
            AmountFinanced = amountFinanced,
            Apr = input.Finance?.Apr,
            TermMonths = input.Finance?.TermMonths,
            Payment = schedule?.Payment,
            PaymentCount = schedule?.PaymentCount,
            FinanceCharge = schedule?.FinanceCharge,
            TotalOfPayments = schedule?.TotalOfPayments,
            PaymentMethod = input.Finance?.Method,
            VehicleCost = input.VehicleCost,
            FrontGross = frontGross,
            BackGross = backGross,
            TotalGross = totalGross,
            LoanToValue = loanToValue,
            Steps = steps,
            Advisories = advisories,
        };
    }

    /// <summary>The refusals, all of which are better than a plausible wrong number.</summary>
    private static Error? Validate(DealInput input, TaxRuleSet rules)
    {
        // The one that matters most. Every rule set seeded today fails it.
        if (!rules.IsApproved)
        {
            return Error.Validation(
                "deal.rule_set_unapproved",
                "The tax and fee rules for this jurisdiction have not been reviewed and approved, " +
                "so a deal cannot be priced. A rule set must cite a primary source and carry a " +
                "named approver before the engine will use it.");
        }

        if (!rules.CoversDate(input.DealDate))
        {
            return Error.Validation(
                "deal.rule_set_not_effective",
                $"Rule set version {rules.Version} does not cover {input.DealDate:yyyy-MM-dd}. " +
                "A deal is priced with the rules in force on the day it was written.");
        }

        if (input.VehiclePrice.IsNegative)
        {
            return Error.Validation("deal.negative_price", "A vehicle price cannot be negative.");
        }

        if (input.LineItems.Any(line => line.Amount.IsNegative))
        {
            return Error.Validation(
                "deal.negative_line_item",
                "Line item amounts are always positive. A discount reduces the price because it " +
                "is a discount, not because it carries a minus sign.");
        }

        if (input.Finance is { } finance)
        {
            if (finance.TermMonths <= 0)
            {
                return Error.Validation("deal.invalid_term", "A financed deal needs a term of at least one month.");
            }

            if (finance.Apr.IsNegative)
            {
                return Error.Validation("deal.negative_apr", "An APR cannot be negative.");
            }
        }

        return null;
    }

    /// <summary>
    /// Works out what the tax rate is applied to.
    /// </summary>
    /// <remarks>
    /// This is the single most jurisdiction-dependent number in the deal, and the
    /// reason the rule set is data rather than code. Three independent decisions
    /// combine here — whether accessories are taxed, whether a rebate reduces the
    /// base, and how a trade is credited — and the launch states do not agree on
    /// any of them.
    /// </remarks>
    private static Money TaxableAmount(
        Money sellingPrice,
        Money accessories,
        Money unconditionalRebates,
        Money conditionalRebates,
        Money tradeAllowance,
        DealInput input,
        TaxRuleSet rules,
        out string note)
    {
        var notes = new List<string>();
        var basis = sellingPrice;

        if (!rules.AccessoriesTaxable && !accessories.IsZero)
        {
            basis -= accessories;
            notes.Add("accessories excluded");
        }

        if (rules.RebateTreatment == RebateTaxTreatment.NotTaxable)
        {
            // Conditional incentives reduce the base only once claimed. Until then
            // the conservative reading is that the customer pays tax on them, and
            // being conservative about a tax base is the correct direction to err.
            basis -= unconditionalRebates;
            notes.Add("rebates deducted");

            if (!conditionalRebates.IsZero)
            {
                notes.Add("conditional incentives NOT deducted pending eligibility");
            }
        }

        var credit = rules.TradeInCredit switch
        {
            TradeInCreditPolicy.Full => tradeAllowance,
            TradeInCreditPolicy.Capped => rules.TradeInCreditCap is { } cap && tradeAllowance > cap
                ? cap
                : tradeAllowance,
            _ => Money.Zero,
        };

        if (!credit.IsZero)
        {
            basis -= credit;
            notes.Add(rules.TradeInCredit == TradeInCreditPolicy.Capped
                ? $"trade credit of {credit} applied, capped at {rules.TradeInCreditCap}"
                : $"trade credit of {credit} applied");
        }

        // Taxable fees are added after the credits, not before: a jurisdiction that
        // taxes the doc fee taxes it whether or not there is a trade.
        var taxableFees = Sum(input.LineItems.Where(line => IsFee(line) && IsTaxableFee(line, rules)));
        if (!taxableFees.IsZero)
        {
            basis += taxableFees;
            notes.Add($"taxable fees of {taxableFees} included");
        }

        // A negative base is not a refund. It means credits exceeded the price, and
        // no jurisdiction pays tax back on that.
        if (basis.IsNegative)
        {
            basis = Money.Zero;
            notes.Add("credits exceeded the price, so the taxable amount is floored at zero");
        }

        note = notes.Count == 0 ? "Full selling price." : string.Join("; ", notes) + ".";
        return basis;
    }

    private static Money ComputeTax(
        Money taxable, TaxRuleSet rules, DealInput input, List<string> advisories)
    {
        // A tax override replaces the computed figure outright. It exists because
        // an out-of-state or exempt sale is real, and it is a line item so that the
        // override is on the contract and in the audit trail rather than being a
        // number someone typed over the top.
        var over = input.LineItems.FirstOrDefault(line => line.Kind == LineItemKind.TaxOverride);
        if (over is not null)
        {
            advisories.Add(
                $"Sales tax was overridden to {over.Amount} ({over.Description}). The computed " +
                "figure was not used.");
            return over.Amount;
        }

        var tax = Money.FromDecimal(taxable.Amount * rules.CombinedRate.AsFraction);

        if (rules.MaximumTax is { } cap && tax > cap)
        {
            advisories.Add($"Sales tax was capped at {cap} by the jurisdiction's maximum.");
            return cap;
        }

        return tax;
    }

    /// <summary>Adds up the fees, and notices when one is over its statutory cap.</summary>
    private static (Money Total, Money StateFees) SumFees(
        DealInput input,
        TaxRuleSet rules,
        List<CalculationStep> steps,
        List<string> advisories,
        ref int order)
    {
        var total = Money.Zero;
        var stateFees = Money.Zero;

        foreach (var fee in input.LineItems.Where(IsFee).OrderBy(line => line.Code, StringComparer.Ordinal))
        {
            total += fee.Amount;

            var rule = rules.FeeFor(fee.Code);
            var isStateFee = rule?.IsStateFee ?? fee.IsStateFee;

            if (isStateFee)
            {
                stateFees += fee.Amount;
            }

            // Noticed, not blocked. Whether exceeding a cap prevents the deal is a
            // regulatory decision made by the compliance engine against a rule with
            // a citation — not something the calculator gets to decide.
            if (rule?.StatutoryCap is { } cap && fee.Amount > cap)
            {
                advisories.Add(
                    $"{fee.Description} is {fee.Amount}, above the {cap} cap for {fee.Code} in " +
                    "this jurisdiction.");
            }

            steps.Add(new CalculationStep(
                ++order,
                $"fee.{fee.Code ?? "other"}",
                fee.Description,
                fee.Amount,
                null,
                isStateFee ? "Passed through to the state." : null));
        }

        return (total, stateFees);
    }

    /// <summary>
    /// Front, back and total gross.
    /// </summary>
    /// <remarks>
    /// Computed against the cost supplied on the input, which the application layer
    /// fills from <c>inventory.vehicle_cost</c> at snapshot time. Freezing it here
    /// is deliberate: a later cost correction must not silently restate a
    /// commission that has already been reported and possibly paid.
    /// </remarks>
    private static (Money? Front, Money? Back, Money? Total) ComputeGross(
        DealInput input,
        Money sellingPrice,
        Money products,
        Money totalFees,
        Money stateFees,
        List<CalculationStep> steps,
        ref int order)
    {
        if (input.VehicleCost is not { } cost)
        {
            return (null, null, null);
        }

        var front = sellingPrice - cost;

        // Back-end gross is product margin plus reserve. Where a product's cost is
        // unknown the whole amount would overstate the margin, so an unknown cost
        // contributes nothing rather than contributing everything.
        var productMargin = Money.Zero;
        foreach (var product in input.LineItems.Where(IsProduct))
        {
            if (product.Cost is { } productCost)
            {
                productMargin += product.Amount - productCost;
            }
        }

        // Dealer fees are dealer revenue; state fees are a pass-through and are not.
        var dealerFees = totalFees - stateFees;
        var reserve = input.Finance?.ReserveAmount ?? Money.Zero;

        var back = productMargin + dealerFees + reserve;
        var total = front + back;

        steps.Add(new CalculationStep(++order, "gross.front", "Front-end gross", front));
        steps.Add(new CalculationStep(++order, "gross.back", "Back-end gross", back));
        steps.Add(new CalculationStep(++order, "gross.total", "Total gross", total, total));

        return (front, back, total);
    }

    private static Percentage? ComputeLoanToValue(Money amountFinanced, Money sellingPrice, bool financed)
    {
        if (!financed || sellingPrice.Amount <= 0m)
        {
            return null;
        }

        return Percentage.FromPercent(amountFinanced.Amount / sellingPrice.Amount * 100m);
    }

    private static bool IsFee(DealLineItem line) => line.Kind == LineItemKind.Fee;

    private static bool IsTaxableFee(DealLineItem line, TaxRuleSet rules) =>
        rules.FeeFor(line.Code)?.IsTaxable ?? line.IsTaxable;

    private static bool IsProduct(DealLineItem line) => line.Kind
        is LineItemKind.Product
        or LineItemKind.ServiceContract
        or LineItemKind.Gap
        or LineItemKind.Warranty;

    private static Money SumOf(DealInput input, LineItemKind kind) =>
        Sum(input.LineItems.Where(line => line.Kind == kind));

    private static Money Sum(IEnumerable<DealLineItem> lines)
    {
        var total = Money.Zero;

        foreach (var line in lines)
        {
            total += line.Amount;
        }

        return total;
    }
}
