using System.Globalization;
using MautoDesk.SharedKernel;

namespace MautoDesk.Inventory.Domain;

/// <summary>
/// A 17-character Vehicle Identification Number.
/// </summary>
/// <remarks>
/// <para>
/// Validation is deliberately strict about the alphabet and deliberately
/// lenient about the check digit. I, O and Q are excluded from the VIN alphabet
/// by ISO 3779 precisely because they are confusable with 1, 0 and 0 — a dealer
/// mistyping a windshield plate is the common case, so rejecting them with an
/// explanation is genuinely helpful.
/// </para>
/// <para>
/// The North American check digit (position 9) is validated but a mismatch is
/// reported as a <em>warning</em>, not a rejection. Pre-1981 vehicles, imports,
/// and some trailers legitimately fail it, and a DMS that refuses to book a real
/// car sitting on the lot is worse than useless. See <see cref="HasValidCheckDigit"/>.
/// </para>
/// </remarks>
public readonly record struct Vin
{
    public const int Length = 17;

    /// <summary>Letters excluded by ISO 3779 because they are confusable with digits.</summary>
    public const string ExcludedLetters = "IOQ";

    private Vin(string value) => Value = value;

    public string Value { get; }

    public static Result<Vin> Create(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Error.Validation("vin.required", "A VIN is required.", "vin");
        }

        var normalized = candidate.Trim().ToUpperInvariant();

        if (normalized.Length != Length)
        {
            return Error.Validation(
                "vin.length",
                $"A VIN is exactly {Length} characters; this one has {normalized.Length}.",
                "vin");
        }

        foreach (var character in normalized)
        {
            if (ExcludedLetters.Contains(character, StringComparison.Ordinal))
            {
                return Error.Validation(
                    "vin.excluded_letter",
                    $"'{character}' is not a valid VIN character. VINs never contain I, O or Q — " +
                    "if the plate looks like one of those, it is a 1 or a 0.",
                    "vin");
            }

            if (!char.IsAsciiLetterOrDigit(character))
            {
                return Error.Validation(
                    "vin.character",
                    $"'{character}' is not a valid VIN character. A VIN contains only letters and digits.",
                    "vin");
            }
        }

        return new Vin(normalized);
    }

    /// <summary>The last six characters — how a VIN is spoken on a lot.</summary>
    public string Last6 => Value[^6..];

    /// <summary>
    /// Whether position 9 matches the ISO 3779 check digit.
    /// </summary>
    /// <remarks>
    /// Advisory only. A false here means "double-check this", not "reject this".
    /// The transliteration table and weights are fixed by the standard.
    /// </remarks>
    public bool HasValidCheckDigit
    {
        get
        {
            ReadOnlySpan<int> weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];
            var sum = 0;

            for (var i = 0; i < Length; i++)
            {
                var transliterated = Transliterate(Value[i]);
                if (transliterated < 0)
                {
                    return false;
                }

                sum += transliterated * weights[i];
            }

            var remainder = sum % 11;
            var expected = remainder == 10 ? 'X' : (char)('0' + remainder);
            return Value[8] == expected;
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(Vin vin) => vin.Value;

    /// <summary>ISO 3779 transliteration; -1 for a character outside the alphabet.</summary>
    private static int Transliterate(char character)
    {
        if (char.IsAsciiDigit(character))
        {
            return character - '0';
        }

        return character switch
        {
            'A' or 'J' => 1,
            'B' or 'K' or 'S' => 2,
            'C' or 'L' or 'T' => 3,
            'D' or 'M' or 'U' => 4,
            'E' or 'N' or 'V' => 5,
            'F' or 'W' => 6,
            'G' or 'P' or 'X' => 7,
            'H' or 'Y' => 8,
            'R' or 'Z' => 9,
            _ => -1,
        };
    }
}

/// <summary>A dealer's stock number.</summary>
/// <remarks>
/// Free-form by design: dealers have long-standing conventions (A-1188, 24-0417,
/// or just 88) and a DMS that imposes its own format is a DMS they abandon. The
/// only constraints are non-empty, trimmed, and unique within the tenant.
/// </remarks>
public readonly record struct StockNumber
{
    public const int MaxLength = 50;

    private StockNumber(string value) => Value = value;

    public string Value { get; }

    public static Result<StockNumber> Create(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Error.Validation("stock_number.required", "A stock number is required.", "stockNumber");
        }

        var normalized = candidate.Trim();

        return normalized.Length > MaxLength
            ? Error.Validation(
                "stock_number.length",
                $"A stock number can be at most {MaxLength} characters.",
                "stockNumber")
            : new StockNumber(normalized);
    }

    public override string ToString() => Value;
}

/// <summary>An odometer reading.</summary>
public readonly record struct Mileage
{
    /// <summary>
    /// Above this, the reading is almost certainly a typo.
    /// </summary>
    /// <remarks>
    /// Chosen high on purpose. Long-haul trucks genuinely exceed a million miles,
    /// and rejecting a real vehicle is a worse failure than accepting an
    /// implausible one that a human will notice.
    /// </remarks>
    public const int ImplausibleAbove = 2_000_000;

    private Mileage(int value) => Value = value;

    public int Value { get; }

    public static Result<Mileage> Create(int candidate)
    {
        if (candidate < 0)
        {
            return Error.Validation("mileage.negative", "Mileage cannot be negative.", "mileage");
        }

        return candidate > ImplausibleAbove
            ? Error.Validation(
                "mileage.implausible",
                $"{candidate.ToString("N0", CultureInfo.InvariantCulture)} miles looks like a typo. " +
                "Check the odometer reading.",
                "mileage")
            : new Mileage(candidate);
    }

    public override string ToString() => Value.ToString("N0", CultureInfo.InvariantCulture);
}
