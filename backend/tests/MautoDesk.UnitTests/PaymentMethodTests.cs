using FluentAssertions;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The amortization arithmetic, against figures computed outside this codebase.
/// </summary>
/// <remarks>
/// The anchors are two payments any finance manager can check from a rate sheet
/// or a mortgage table. A test that asserts what the code currently produces
/// proves only that the code is consistent with itself; these assert what the
/// answer <em>is</em>.
/// </remarks>
public sealed class PaymentMethodTests
{
    private static readonly StandardAmortizationMethod Standard = new();

    /// <summary>
    /// $25,000 at 6.00% over 60 months is $483.32 a month.
    /// </summary>
    /// <remarks>
    /// Worked by hand: r = 0.06/12 = 0.005, n = 60, (1.005)^60 = 1.34885015,
    /// so payment = 25000 × 0.005 × 1.34885015 / 0.34885015 = 483.32006, which
    /// rounds to 483.32.
    /// </remarks>
    [Fact]
    public void A_standard_retail_deal_produces_the_published_payment()
    {
        var schedule = Standard.Compute(
            Money.FromDecimal(25_000m), Percentage.FromPercent(6m), 60, PaymentFrequency.Monthly);

        schedule.Payment.Amount.Should().Be(483.32m);
        schedule.PaymentCount.Should().Be(60);
        schedule.TotalOfPayments.Amount.Should().Be(28_999.20m, "60 × 483.32");
        schedule.FinanceCharge.Amount.Should().Be(3_999.20m, "28,999.20 − 25,000.00");
    }

    /// <summary>
    /// $100,000 at 6.00% over 360 months is $599.55 — the textbook mortgage figure.
    /// </summary>
    /// <remarks>
    /// A second anchor at a very different scale and term, because a formula can be
    /// wrong in a way that only shows up when the exponent is large.
    /// </remarks>
    [Fact]
    public void The_formula_holds_over_a_long_term()
    {
        var schedule = Standard.Compute(
            Money.FromDecimal(100_000m), Percentage.FromPercent(6m), 360, PaymentFrequency.Monthly);

        schedule.Payment.Amount.Should().Be(599.55m);
    }

    /// <summary>
    /// A 0% APR does not divide by zero.
    /// </summary>
    /// <remarks>
    /// The amortization formula's denominator is <c>(1+r)ⁿ − 1</c>, which is zero
    /// when r is zero. Captive 0% financing is common enough that leaving this to
    /// the general case would crash on a real deal.
    /// </remarks>
    [Fact]
    public void Zero_percent_financing_divides_the_principal_evenly()
    {
        var schedule = Standard.Compute(
            Money.FromDecimal(12_000m), Percentage.Zero, 48, PaymentFrequency.Monthly);

        schedule.Payment.Amount.Should().Be(250m);
        schedule.FinanceCharge.Amount.Should().Be(0m, "there is no interest at 0%");
        schedule.TotalOfPayments.Amount.Should().Be(12_000m);
    }

    /// <summary>
    /// Add-on interest is not amortization, and the difference is large.
    /// </summary>
    /// <remarks>
    /// $10,000 at a nominal 10% over 24 months: charge = 10,000 × 0.10 × 2 = 2,000,
    /// payment = 12,000 / 24 = 500.00. The same principal, rate and term amortized
    /// is $461.45 — so quoting the wrong method is roughly $39 a month, every month.
    /// This is why the method belongs to the lender program version and is pinned
    /// onto the snapshot rather than assumed.
    /// </remarks>
    [Fact]
    public void Precomputed_interest_costs_the_customer_materially_more()
    {
        var principal = Money.FromDecimal(10_000m);
        var apr = Percentage.FromPercent(10m);

        var precomputed = new PrecomputedInterestMethod()
            .Compute(principal, apr, 24, PaymentFrequency.Monthly);

        precomputed.Payment.Amount.Should().Be(500m);
        precomputed.FinanceCharge.Amount.Should().Be(2_000m);

        var amortized = Standard.Compute(principal, apr, 24, PaymentFrequency.Monthly);

        amortized.Payment.Amount.Should().BeLessThan(
            precomputed.Payment.Amount,
            "add-on interest charges on the original principal rather than the declining balance");
    }

    /// <summary>Simple interest quotes the same scheduled payment as amortization.</summary>
    /// <remarks>
    /// It is a separate strategy because the two differ in how interest accrues
    /// afterwards, which matters for payoff quotes. The scheduled payment does not
    /// differ, and asserting that keeps the distinction honest rather than
    /// decorative.
    /// </remarks>
    [Fact]
    public void Simple_interest_schedules_the_same_payment_as_amortization()
    {
        var principal = Money.FromDecimal(18_500m);
        var apr = Percentage.FromPercent(9.49m);

        var method = new SimpleInterestMethod();

        var simple = method.Compute(principal, apr, 66, PaymentFrequency.Monthly);
        var standard = Standard.Compute(principal, apr, 66, PaymentFrequency.Monthly);

        simple.Payment.Should().Be(standard.Payment);

        // The kind is what gets recorded on the snapshot, so a deal replayed years
        // later resolves this strategy rather than the amortized one it happens to
        // agree with today.
        method.Kind.Should().Be(PaymentMethodKind.SimpleInterest);
        PaymentMethods.For(PaymentMethodKind.SimpleInterest).Kind
            .Should().Be(PaymentMethodKind.SimpleInterest);
    }

    /// <summary>
    /// A term in months is not a payment count.
    /// </summary>
    /// <remarks>
    /// Biweekly is 26 payments a year and weekly 52 — so a 60-month term is 130 and
    /// 260 payments, not 120 and 240. Treating a biweekly term as 120 payments
    /// roughly halves the quoted payment, which is exactly the sort of error that
    /// nobody questions because it is good news.
    /// </remarks>
    [Theory]
    [InlineData(PaymentFrequency.Monthly, 60, 60)]
    [InlineData(PaymentFrequency.Semimonthly, 60, 120)]
    [InlineData(PaymentFrequency.Biweekly, 60, 130)]
    [InlineData(PaymentFrequency.Weekly, 60, 260)]
    [InlineData(PaymentFrequency.Biweekly, 12, 26)]
    public void Payment_counts_follow_the_frequency(
        PaymentFrequency frequency, int termMonths, int expected) =>
        PaymentMath.PaymentCount(termMonths, frequency).Should().Be(expected);

    /// <summary>The disclosed figures are internally consistent, at every rate and term.</summary>
    /// <remarks>
    /// The finance charge must be exactly what is left after the principal, and the
    /// total exactly the payment times the count. Deriving the charge independently
    /// would let it disagree with the payments by a few cents — and a Truth in
    /// Lending disclosure that does not add up is a defect with a statute attached.
    /// </remarks>
    [Theory]
    [InlineData(15_000, 3.9, 36)]
    [InlineData(27_500, 7.99, 72)]
    [InlineData(41_250, 12.25, 84)]
    [InlineData(9_800, 21.99, 48)]
    public void The_disclosed_figures_reconcile(decimal principalAmount, decimal aprPercent, int term)
    {
        var principal = Money.FromDecimal(principalAmount);

        var schedule = Standard.Compute(
            principal, Percentage.FromPercent(aprPercent), term, PaymentFrequency.Monthly);

        schedule.TotalOfPayments.Amount.Should().Be(
            schedule.Payment.Amount * schedule.PaymentCount,
            "the customer pays the rounded payment, so the total follows from it");

        schedule.FinanceCharge.Amount.Should().Be(
            schedule.TotalOfPayments.Amount - principal.Amount,
            "the finance charge is what is left after the principal");
    }

    /// <summary>A higher rate is a higher payment, monotonically.</summary>
    [Fact]
    public void A_higher_rate_always_costs_more()
    {
        var principal = Money.FromDecimal(22_000m);
        var previous = 0m;

        foreach (var rate in new[] { 0m, 2.9m, 5.99m, 9.99m, 14.5m, 24.99m })
        {
            var payment = Standard
                .Compute(principal, Percentage.FromPercent(rate), 60, PaymentFrequency.Monthly)
                .Payment.Amount;

            payment.Should().BeGreaterThan(previous, "the payment at {0}% must exceed the one below it", rate);
            previous = payment;
        }
    }

    /// <summary>A longer term is a smaller payment and more interest.</summary>
    [Fact]
    public void A_longer_term_lowers_the_payment_and_raises_the_cost()
    {
        var principal = Money.FromDecimal(30_000m);
        var apr = Percentage.FromPercent(8m);

        var short60 = Standard.Compute(principal, apr, 60, PaymentFrequency.Monthly);
        var long84 = Standard.Compute(principal, apr, 84, PaymentFrequency.Monthly);

        long84.Payment.Amount.Should().BeLessThan(short60.Payment.Amount);
        long84.FinanceCharge.Amount.Should().BeGreaterThan(
            short60.FinanceCharge.Amount,
            "stretching the term is what a customer pays for a smaller payment");
    }

    /// <summary>Exponentiation is exact for the cases the engine actually uses.</summary>
    [Theory]
    [InlineData(2, 0, 1)]
    [InlineData(2, 10, 1024)]
    [InlineData(1.5, 2, 2.25)]
    public void Pow_matches_hand_computed_values(decimal value, int exponent, decimal expected) =>
        PaymentMath.Pow(value, exponent).Should().Be(expected);

    /// <summary>Nothing to finance produces nothing, rather than a divide-by-zero.</summary>
    [Fact]
    public void A_zero_principal_produces_a_zero_schedule()
    {
        var schedule = Standard.Compute(
            Money.Zero, Percentage.FromPercent(6m), 60, PaymentFrequency.Monthly);

        schedule.Payment.Should().Be(Money.Zero);
        schedule.FinanceCharge.Should().Be(Money.Zero);
    }
}
