using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>The result of turning a principal into a payment schedule.</summary>
/// <param name="Payment">The scheduled payment, rounded to the cent.</param>
/// <param name="PaymentCount">How many payments there are.</param>
/// <param name="FinanceCharge">Total interest over the term.</param>
/// <param name="TotalOfPayments">Payment times count — what the customer pays in all.</param>
public sealed record PaymentSchedule(
    Money Payment,
    int PaymentCount,
    Money FinanceCharge,
    Money TotalOfPayments);

/// <summary>
/// Turns a principal, a rate and a term into a payment.
/// </summary>
/// <remarks>
/// <para>
/// A strategy rather than a method, because lenders do not agree. The lender
/// program version selects the implementation, and the selection is recorded on
/// the calculation snapshot — so replaying a 2026 deal in 2029 uses the
/// arithmetic that applied in 2026, not whichever one the lender uses by then.
/// </para>
/// <para>
/// Every implementation is pure: same inputs, same output, no clock, no I/O, and
/// <see cref="decimal"/> throughout. An architecture test fails the build if a
/// <see cref="double"/> becomes reachable from here.
/// </para>
/// </remarks>
public interface IPaymentMethod
{
    /// <summary>Which lender arithmetic this implements.</summary>
    public PaymentMethodKind Kind { get; }

    /// <summary>Computes the schedule for a principal over a term.</summary>
    public PaymentSchedule Compute(Money principal, Percentage apr, int termMonths, PaymentFrequency frequency);
}

/// <summary>Shared arithmetic for the payment strategies.</summary>
public static class PaymentMath
{
    /// <summary>How many payments a term produces at a given frequency.</summary>
    /// <remarks>
    /// Biweekly is 26 a year and weekly 52, so a 60-month term is 130 and 260
    /// payments respectively — not 120 and 240. Getting this wrong understates the
    /// payment by roughly half, which is the kind of error that looks like good news.
    /// </remarks>
    public static int PaymentCount(int termMonths, PaymentFrequency frequency) => frequency switch
    {
        PaymentFrequency.Monthly => termMonths,
        PaymentFrequency.Semimonthly => termMonths * 2,

        // 26 and 52 per year. Integer arithmetic on months would truncate, so the
        // multiplication happens before the division.
        PaymentFrequency.Biweekly => (int)Math.Round(termMonths * 26m / 12m, MidpointRounding.AwayFromZero),
        PaymentFrequency.Weekly => (int)Math.Round(termMonths * 52m / 12m, MidpointRounding.AwayFromZero),
        _ => termMonths,
    };

    /// <summary>Payments per year at a given frequency.</summary>
    public static int PeriodsPerYear(PaymentFrequency frequency) => frequency switch
    {
        PaymentFrequency.Monthly => 12,
        PaymentFrequency.Semimonthly => 24,
        PaymentFrequency.Biweekly => 26,
        PaymentFrequency.Weekly => 52,
        _ => 12,
    };

    /// <summary>The periodic rate: the annual rate divided by the number of periods.</summary>
    /// <remarks>
    /// Deliberately not rounded. Rounding the periodic rate before compounding it
    /// over sixty periods moves the payment by cents, and the rounding belongs on
    /// the resulting money, once.
    /// </remarks>
    public static decimal PeriodicRate(Percentage apr, PaymentFrequency frequency) =>
        apr.AsFraction / PeriodsPerYear(frequency);

    /// <summary>
    /// Raises a decimal to a non-negative integer power.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Math.Pow(double, double)"/> would be one line and would put a
    /// binary floating-point value in the middle of a payment calculation. The
    /// architecture test that bans <see cref="double"/> from this assembly exists
    /// precisely so that shortcut is not available.
    /// </para>
    /// <para>
    /// Exponentiation by squaring rather than a loop of <paramref name="exponent"/>
    /// multiplications: fewer operations means less accumulated rounding in the
    /// last digits of the <see cref="decimal"/>, and the result is deterministic
    /// either way.
    /// </para>
    /// </remarks>
    public static decimal Pow(decimal value, int exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);

        var result = 1m;
        var factor = value;
        var remaining = exponent;

        while (remaining > 0)
        {
            if ((remaining & 1) == 1)
            {
                result *= factor;
            }

            remaining >>= 1;

            if (remaining > 0)
            {
                factor *= factor;
            }
        }

        return result;
    }
}

/// <summary>
/// The standard amortization formula.
/// </summary>
/// <remarks>
/// <code>
///            P · r · (1 + r)ⁿ
/// Payment = ──────────────────
///             (1 + r)ⁿ − 1
/// </code>
/// where P is the principal, r the periodic rate and n the number of payments.
/// A zero rate degenerates to <c>P / n</c>, which the formula cannot express
/// because the denominator is zero — so it is handled explicitly rather than
/// left to produce a divide-by-zero on the one deal that has 0% financing.
/// </remarks>
public sealed class StandardAmortizationMethod : IPaymentMethod
{
    /// <inheritdoc />
    public PaymentMethodKind Kind => PaymentMethodKind.StandardAmortization;

    /// <inheritdoc />
    public PaymentSchedule Compute(Money principal, Percentage apr, int termMonths, PaymentFrequency frequency)
    {
        var count = PaymentMath.PaymentCount(termMonths, frequency);

        if (count <= 0 || principal.Amount <= 0m)
        {
            return new PaymentSchedule(Money.Zero, count, Money.Zero, Money.Zero);
        }

        var rate = PaymentMath.PeriodicRate(apr, frequency);

        var payment = rate == 0m
            ? Money.FromDecimal(principal.Amount / count)
            : ComputeWithInterest(principal.Amount, rate, count);

        // Total of payments is the payment times the count. It is NOT the
        // principal plus a separately computed finance charge: the customer pays
        // the rounded payment, so the rounding is part of what they owe, and the
        // finance charge is what is left over after the principal.
        var total = Money.FromDecimal(payment.Amount * count);
        var charge = total - principal;

        return new PaymentSchedule(payment, count, charge, total);
    }

    private static Money ComputeWithInterest(decimal principal, decimal rate, int count)
    {
        var growth = PaymentMath.Pow(1m + rate, count);
        return Money.FromDecimal(principal * rate * growth / (growth - 1m));
    }
}

/// <summary>
/// Simple interest, where interest accrues on the outstanding balance.
/// </summary>
/// <remarks>
/// <para>
/// The <em>scheduled</em> payment is the same amortization formula: a simple
/// interest contract is quoted from the same maths. What differs is what happens
/// afterwards — interest accrues daily on the balance, so paying early reduces
/// total interest and paying late increases it, neither of which is true of a
/// pre-computed contract.
/// </para>
/// <para>
/// It is a distinct strategy rather than an alias because the difference is real
/// and appears the moment payoff quotes and early-payoff figures are built. Making
/// it an alias now would mean discovering later that every historical snapshot
/// recorded the wrong method name.
/// </para>
/// </remarks>
public sealed class SimpleInterestMethod : IPaymentMethod
{
    private static readonly StandardAmortizationMethod Schedule = new();

    /// <inheritdoc />
    public PaymentMethodKind Kind => PaymentMethodKind.SimpleInterest;

    /// <inheritdoc />
    public PaymentSchedule Compute(Money principal, Percentage apr, int termMonths, PaymentFrequency frequency) =>
        Schedule.Compute(principal, apr, termMonths, frequency);
}

/// <summary>
/// Pre-computed, or add-on, interest.
/// </summary>
/// <remarks>
/// <para>
/// The finance charge is computed on the original principal for the whole term
/// and added before dividing:
/// <code>
/// charge  = P × r × years
/// payment = (P + charge) / n
/// </code>
/// </para>
/// <para>
/// This is <b>not</b> the same as amortization at the same nominal rate, and the
/// difference runs the wrong way for the customer: because the charge is
/// calculated on the full principal rather than the declining balance, the
/// effective rate is close to double the nominal one. A deal quoted with the
/// wrong method here is off by tens of dollars a month, which is why the method
/// belongs to the lender program version and is pinned onto the snapshot.
/// </para>
/// </remarks>
public sealed class PrecomputedInterestMethod : IPaymentMethod
{
    /// <inheritdoc />
    public PaymentMethodKind Kind => PaymentMethodKind.Precomputed;

    /// <inheritdoc />
    public PaymentSchedule Compute(Money principal, Percentage apr, int termMonths, PaymentFrequency frequency)
    {
        var count = PaymentMath.PaymentCount(termMonths, frequency);

        if (count <= 0 || principal.Amount <= 0m)
        {
            return new PaymentSchedule(Money.Zero, count, Money.Zero, Money.Zero);
        }

        var years = termMonths / 12m;
        var charge = Money.FromDecimal(principal.Amount * apr.AsFraction * years);
        var payment = Money.FromDecimal((principal.Amount + charge.Amount) / count);

        var total = Money.FromDecimal(payment.Amount * count);

        // Recomputed from the rounded payment rather than reusing `charge`, for
        // the same reason as the amortized case: the customer pays the rounded
        // payment, so the rounding belongs in the disclosed finance charge.
        return new PaymentSchedule(payment, count, total - principal, total);
    }
}

/// <summary>Picks the strategy a lender program calls for.</summary>
/// <remarks>
/// A static lookup rather than dependency injection: these are pure functions
/// with no state and no configuration, and resolving them through a container
/// would put the deal engine's determinism at the mercy of a registration.
/// </remarks>
public static class PaymentMethods
{
    private static readonly Dictionary<PaymentMethodKind, IPaymentMethod> Registry =
        new()
        {
            [PaymentMethodKind.StandardAmortization] = new StandardAmortizationMethod(),
            [PaymentMethodKind.SimpleInterest] = new SimpleInterestMethod(),
            [PaymentMethodKind.Precomputed] = new PrecomputedInterestMethod(),
        };

    /// <summary>Returns the strategy for a kind.</summary>
    public static IPaymentMethod For(PaymentMethodKind kind) => Registry[kind];
}
