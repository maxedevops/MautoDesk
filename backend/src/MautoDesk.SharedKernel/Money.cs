using System.Globalization;

namespace MautoDesk.SharedKernel;

/// <summary>
/// A monetary amount and its currency.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="decimal"/> — a 128-bit base-10 type — never
/// <see cref="double"/>. This is the single most important type in the system:
/// the deal engine computes tax and fees with it, and those numbers are printed
/// on a retail contract a consumer signs. A binary floating-point representation
/// cannot store 0.1 exactly, and a cent of drift on a signed contract is a legal
/// problem, not a rounding curiosity.
/// </para>
/// <para>
/// Rounding is always explicit. There is no implicit conversion from
/// <see cref="double"/> or <see cref="float"/>, and an architecture test fails
/// the build if either type becomes reachable from a Sales domain type.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Currency codes the platform supports. USD only at launch.</summary>
    public const string Usd = "USD";

    /// <summary>Statutory money rounding: half away from zero, to the cent.</summary>
    /// <remarks>
    /// Deliberately NOT banker's rounding. .NET's default
    /// <see cref="MidpointRounding.ToEven"/> would round $0.125 to $0.12, which
    /// does not match how a dealer's paperwork — or a state tax table — computes
    /// a half-cent. Every rounding call in this system names the mode.
    /// </remarks>
    public const MidpointRounding RoundingMode = MidpointRounding.AwayFromZero;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Zero { get; } = new(0m, Usd);

    /// <summary>Creates an amount, rounding to the cent.</summary>
    public static Money FromDecimal(decimal amount, string currency = Usd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        return new Money(Math.Round(amount, 2, RoundingMode), currency.ToUpperInvariant());
    }

    /// <summary>
    /// Parses the decimal string used on the wire, e.g. <c>"28995.00"</c>.
    /// </summary>
    /// <remarks>
    /// The API contract transports money as a string precisely so that no
    /// JavaScript client can round it through an IEEE-754 double on the way in
    /// or out. Parsing is invariant-culture and rejects group separators, so
    /// <c>"28,995.00"</c> is a validation failure rather than a silent 28.995.
    /// </remarks>
    public static Result<Money> TryParse(string? value, string currency = Usd)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<Money>.Failure(Error.Validation("money.empty", "An amount is required."));
        }

        const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        if (!decimal.TryParse(value, Styles, CultureInfo.InvariantCulture, out var parsed))
        {
            return Result<Money>.Failure(Error.Validation(
                "money.format",
                $"'{value}' is not a valid amount. Use a plain decimal such as 28995.00."));
        }

        return Result<Money>.Success(FromDecimal(parsed, currency));
    }

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>Multiplies by a rate, rounding the result to the cent.</summary>
    public static Money operator *(Money left, decimal factor) =>
        FromDecimal(left.Amount * factor, left.Currency);

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount > right.Amount;
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount < right.Amount;
    }

    public static bool operator >=(Money left, Money right) => !(left < right);

    public static bool operator <=(Money left, Money right) => !(left > right);

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Renders the wire format: a plain decimal string with two places.</summary>
    public override string ToString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine {left.Currency} with {right.Currency}. " +
                "Currency conversion is an explicit operation, never an implicit one.");
        }
    }
}
