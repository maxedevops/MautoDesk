using System.Globalization;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Domain;

/// <summary>
/// A rate, carrying its unit in the type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> An APR is written "8.99%" by a dealer, stored as
/// <c>8.990000</c> in <c>numeric(9,6)</c>, and used as <c>0.0899</c> in the
/// amortization formula. A tax rate is written "4.5%", stored the same way, and
/// applied as <c>0.045</c>. Every one of those conversions is a place to be wrong
/// by a factor of one hundred, and a payment that is 100× too small looks
/// plausible enough on a screen to reach a contract.
/// </para>
/// <para>
/// So the unit is not a convention anyone has to remember. A
/// <see cref="Percentage"/> is always the percent form; the fraction is only
/// reachable through <see cref="AsFraction"/>, which is named after what it
/// returns. There is no implicit conversion from <see cref="decimal"/> in either
/// direction, so a bare number cannot become a rate by accident.
/// </para>
/// <para>
/// Backed by <see cref="decimal"/> like everything else that touches money.
/// Precision is six decimal places, matching <c>numeric(9,6)</c> — enough for a
/// combined state-plus-local rate quoted to a ten-thousandth of a percent.
/// </para>
/// </remarks>
public readonly record struct Percentage : IComparable<Percentage>
{
    /// <summary>Places kept, matching <c>numeric(9,6)</c> in the schema.</summary>
    public const int Scale = 6;

    private Percentage(decimal percent) => Percent = percent;

    /// <summary>The percent form: 8.99 means 8.99%.</summary>
    public decimal Percent { get; }

    /// <summary>Zero percent.</summary>
    public static Percentage Zero { get; }

    /// <summary>Creates a rate from its percent form, so 4.5 means 4.5%.</summary>
    public static Percentage FromPercent(decimal percent) =>
        new(Math.Round(percent, Scale, Money.RoundingMode));

    /// <summary>Creates a rate from its fractional form, so 0.045 means 4.5%.</summary>
    /// <remarks>
    /// Present for the rare caller that genuinely holds a fraction. Named so that
    /// choosing it is a decision rather than a default.
    /// </remarks>
    public static Percentage FromFraction(decimal fraction) =>
        new(Math.Round(fraction * 100m, Scale, Money.RoundingMode));

    /// <summary>Parses the wire form, a plain decimal string in percent.</summary>
    /// <remarks>
    /// Like <see cref="Money.TryParse"/>, invariant culture and no group
    /// separators — <c>"8,99"</c> is a validation failure rather than a silent 8.
    /// </remarks>
    public static Result<Percentage> TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<Percentage>.Failure(Error.Validation("rate.empty", "A rate is required."));
        }

        const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        if (!decimal.TryParse(value, Styles, CultureInfo.InvariantCulture, out var parsed))
        {
            return Result<Percentage>.Failure(Error.Validation(
                "rate.format",
                $"'{value}' is not a valid rate. Use a plain decimal percentage such as 8.99."));
        }

        return Result<Percentage>.Success(FromPercent(parsed));
    }

    /// <summary>The fractional form, for arithmetic: 4.5% becomes 0.045.</summary>
    /// <remarks>
    /// Deliberately <b>not</b> rounded. Rounding a rate before multiplying is how
    /// a tax computation drifts; the rounding belongs on the resulting money, once,
    /// at the end.
    /// </remarks>
    public decimal AsFraction => Percent / 100m;

    /// <summary>True when the rate is zero — a 0% APR is a real thing, not a missing value.</summary>
    public bool IsZero => Percent == 0m;

    /// <summary>True when the rate is below zero, which no valid rate is.</summary>
    public bool IsNegative => Percent < 0m;

    /// <inheritdoc />
    public int CompareTo(Percentage other) => Percent.CompareTo(other.Percent);

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(Percentage left, Percentage right) => left.Percent > right.Percent;

    /// <summary>Less-than comparison.</summary>
    public static bool operator <(Percentage left, Percentage right) => left.Percent < right.Percent;

    /// <summary>Greater-than-or-equal comparison.</summary>
    public static bool operator >=(Percentage left, Percentage right) => !(left < right);

    /// <summary>Less-than-or-equal comparison.</summary>
    public static bool operator <=(Percentage left, Percentage right) => !(left > right);

    /// <summary>Adds two rates, as a state and a local rate combine.</summary>
    public static Percentage operator +(Percentage left, Percentage right) =>
        FromPercent(left.Percent + right.Percent);

    /// <summary>Adds two rates. The named form, for callers that cannot use the operator.</summary>
    public static Percentage Add(Percentage left, Percentage right) => left + right;

    /// <summary>Renders the wire form: a plain decimal string in percent.</summary>
    public override string ToString() => Percent.ToString("0.######", CultureInfo.InvariantCulture);
}
