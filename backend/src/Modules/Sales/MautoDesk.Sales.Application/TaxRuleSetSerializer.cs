using System.Text.Json;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Sales.Application;

/// <summary>The database row, before it has been made sense of.</summary>
/// <param name="RuleSetId">The <c>sales.rule_set</c> row.</param>
/// <param name="JurisdictionId">The taxing jurisdiction.</param>
/// <param name="Version">The row's version number.</param>
/// <param name="EffectiveFrom">First date it governs.</param>
/// <param name="EffectiveTo">Last date it governs, exclusive.</param>
/// <param name="IsApproved">Whether a named human approved it.</param>
/// <param name="SourceCitation">What it was read from.</param>
/// <param name="RulesJson">The <c>rules</c> jsonb, verbatim.</param>
public sealed record TaxRuleSetRow(
    Guid RuleSetId,
    Guid JurisdictionId,
    int Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsApproved,
    string SourceCitation,
    string RulesJson);

/// <summary>
/// Turns the <c>rules</c> jsonb into a typed <see cref="TaxRuleSet"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in the application layer rather than infrastructure so it can be
/// tested without a database — and rather than the domain so the engine assembly
/// keeps no dependency on a JSON library.
/// </para>
/// <para>
/// <b>Strict when approved, lenient when not.</b> That asymmetry is the whole
/// point of this class. Every rule set seeded today holds <c>'UNVERIFIED'</c>
/// placeholders; those rows must load without throwing, so the engine can reach
/// its own refusal and report something a human can act on. But an
/// <em>approved</em> row with a value that will not parse is a different thing
/// entirely — silently defaulting a rate to zero there would produce a confident
/// tax of $0.00 on a real contract. So an approved row that does not parse is an
/// error, and an unapproved one is a shrug.
/// </para>
/// </remarks>
public static class TaxRuleSetSerializer
{
    private const string Unverified = "UNVERIFIED";

    /// <summary>Deserializes a row, or explains why it cannot be trusted.</summary>
    public static Result<TaxRuleSet> Deserialize(TaxRuleSetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        JsonElement rules;

        try
        {
            using var document = JsonDocument.Parse(row.RulesJson);
            rules = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Error.Validation(
                "rule_set.malformed",
                $"The rules for rule set version {row.Version} are not valid JSON: {ex.Message}");
        }

        var strict = row.IsApproved;

        // Bands first: whether a flat rate is required at all depends on whether
        // this jurisdiction has them.
        var bands = ReadBands(rules, strict, out var bandError);
        if (bandError is not null)
        {
            return bandError;
        }

        var rounding = ReadTaxRounding(rules, strict, out var roundingError);
        if (roundingError is not null)
        {
            return roundingError;
        }

        // A banded jurisdiction states its rate in the bands, so stateRate must
        // be absent rather than present-and-zero. Demanding it here would make an
        // approved banded rule set impossible to load at all: Oklahoma's excise
        // tax lives entirely in its bands, and requiring a flat rate beside them
        // would be requiring the thing TaxRuleSet.BandError refuses.
        // Read either way, but required only without bands. A banded jurisdiction
        // states its rate in the bands, so demanding a flat rate beside them would
        // make an approved banded rule set impossible to load at all - Oklahoma's
        // excise tax lives entirely in its bands.
        //
        // It is still read when bands exist, rather than skipped: a rule set
        // carrying both is a contradiction somebody should hear about, and
        // ignoring the flat rate would hide it. TaxRuleSet.BandError is what
        // refuses it, so there is one definition of that contradiction and not
        // two.
        var stateRate = ReadRate(rules, "stateRate", strict && bands.Count == 0, out var stateError);

        if (bands.Count == 0 && stateError is not null)
        {
            return stateError;
        }

        var localRate = ReadLocalRate(rules, strict, out var localError);
        if (localError is not null)
        {
            return localError;
        }

        var tradePolicy = ReadTradePolicy(rules, strict, out var tradeError);
        if (tradeError is not null)
        {
            return tradeError;
        }

        var rebateTreatment = ReadRebateTreatment(rules, strict, out var rebateError);
        if (rebateError is not null)
        {
            return rebateError;
        }

        return new TaxRuleSet
        {
            RuleSetId = row.RuleSetId,
            JurisdictionId = row.JurisdictionId,
            Version = row.Version,
            EffectiveFrom = row.EffectiveFrom,
            EffectiveTo = row.EffectiveTo,
            IsApproved = row.IsApproved,
            SourceCitation = row.SourceCitation,
            StateRate = stateRate,
            LocalRate = localRate,
            TradeInCredit = tradePolicy,
            TradeInCreditCap = ReadMoney(rules, "tradeInCreditCap"),
            RebateTreatment = rebateTreatment,
            MaximumTax = ReadMoney(rules, "maximumTax"),
            AccessoriesTaxable = ReadBool(rules, "accessoriesTaxable", defaultValue: true),
            Fees = ReadFees(rules),
            Bands = bands,
            Rounding = rounding,
        };
    }

    /// <summary>
    /// Reads a graduated tax, where the jurisdiction levies one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent for the ordinary flat-rate jurisdiction, so an empty list is the
    /// normal case and not a problem. What is a problem is a <c>bands</c> key that
    /// is present and unreadable on an <em>approved</em> row: Oklahoma's entire
    /// excise tax lives in this array, and defaulting it to empty would fall back
    /// on a <c>stateRate</c> of zero and put $0.00 of tax on a retail contract.
    /// </para>
    /// <para>
    /// Shape errors — bands that do not ascend, an unbounded band that is not
    /// last — are deliberately not checked here. They belong to
    /// <see cref="TaxRuleSet.BandError"/>, so the engine and the approval tool
    /// apply one definition of a valid band set rather than two that can drift.
    /// </para>
    /// </remarks>
    private static List<TaxBand> ReadBands(JsonElement rules, bool strict, out Error? error)
    {
        error = null;

        if (!rules.TryGetProperty("bands", out var bands) || bands.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (bands.ValueKind != JsonValueKind.Array)
        {
            if (strict)
            {
                error = Error.Validation(
                    "rule_set.bands_malformed",
                    "This rule set is approved but its 'bands' is not an array. A graduated tax "
                    + "that cannot be read would silently fall back on a flat rate of zero.");
            }

            return [];
        }

        var result = new List<TaxBand>();
        var position = 0;

        foreach (var band in bands.EnumerateArray())
        {
            position++;

            var rate = Percentage.Zero;
            var rawRate = ReadString(band, "rate");

            if (rawRate is not null && !string.Equals(rawRate, Unverified, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = Percentage.TryParse(rawRate);

                if (parsed.IsFailure)
                {
                    if (strict)
                    {
                        error = Error.Validation(
                            "rule_set.band_rate_malformed",
                            $"This rule set is approved but band {position} has a rate of "
                            + $"'{rawRate}', which is not a rate.");
                        return [];
                    }

                    continue;
                }

                rate = parsed.Value;
            }

            result.Add(new TaxBand(
                ReadMoney(band, "upTo"),
                rate,
                ReadMoney(band, "flat"),
                ReadString(band, "note")));
        }

        return result;
    }

    /// <summary>Reads how the tax line rounds, which is not how the rest of the deal rounds.</summary>
    private static TaxRounding ReadTaxRounding(JsonElement rules, bool strict, out Error? error)
    {
        error = null;

        switch (ReadString(rules, "taxRounding")?.ToLowerInvariant())
        {
            case "dollars":
            case "dollar":
                return TaxRounding.Dollars;
            case "cents":
            case "cent":
            case null:
                // Absent means the cent, which is what every jurisdiction does
                // unless its statute says otherwise. Not an omission.
                return TaxRounding.Cents;
            default:
                if (strict)
                {
                    error = Error.Validation(
                        "rule_set.tax_rounding_unknown",
                        "This rule set is approved but its 'taxRounding' is not one of 'cents' or "
                        + "'dollars'. Rounding a tax the wrong way is a cents-level error on every "
                        + "deal, which is the kind that is found by an auditor rather than a test.");
                }

                return TaxRounding.Cents;
        }
    }

    /// <summary>
    /// Reads the local rate, which is required only where local tax applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent is a legitimate answer: plenty of states levy no local tax on a
    /// vehicle sale, and erroring on a missing <c>localRate</c> would make those
    /// jurisdictions unapprovable. So the rule set says which world it is in, and
    /// <c>localRateSource</c> is that statement.
    /// </para>
    /// <para>
    /// The dangerous combination is the one this does refuse: a rule set that
    /// declares local tax applies and then gives no rate to apply. That would
    /// quietly undercharge every deal in a destination-sourced state, and the
    /// dealer would owe the difference.
    /// </para>
    /// <para>
    /// An <c>UNVERIFIED</c> source is read here as "no local rate yet" rather
    /// than as an error, because such a rule set is refused one layer up — the
    /// approval tool will not let an UNVERIFIED field onto an approved row. The
    /// serializer's job is to read, not to adjudicate.
    /// </para>
    /// </remarks>
    private static Percentage ReadLocalRate(JsonElement rules, bool strict, out Error? error)
    {
        var source = ReadString(rules, "localRateSource")?.ToLowerInvariant();
        var applies = source is "destination" or "origin";

        if (!applies)
        {
            error = null;
            return ReadRate(rules, "localRate", strict: false, out _);
        }

        var rate = ReadRate(rules, "localRate", strict, out error);

        if (error is not null)
        {
            error = Error.Validation(
                "rule_set.local_rate_missing",
                $"This rule set is approved and says local tax is sourced by {source}, but gives "
                + "no localRate to apply. Defaulting it to zero would undercharge every deal in "
                + "the jurisdiction, and the dealer would owe the state the difference.");
        }

        return rate;
    }

    private static Percentage ReadRate(
        JsonElement rules, string name, bool strict, out Error? error)
    {
        error = null;

        var raw = ReadString(rules, name);

        if (raw is null || string.Equals(raw, Unverified, StringComparison.OrdinalIgnoreCase))
        {
            if (strict)
            {
                error = Error.Validation(
                    "rule_set.rate_missing",
                    $"This rule set is approved but its '{name}' is missing or still marked " +
                    "UNVERIFIED. An approved rule set must carry real, sourced values — " +
                    "defaulting it to zero would put a confident wrong tax on a contract.");
            }

            return Percentage.Zero;
        }

        var parsed = Percentage.TryParse(raw);

        if (parsed.IsFailure)
        {
            if (strict)
            {
                error = Error.Validation(
                    "rule_set.rate_malformed",
                    $"This rule set is approved but its '{name}' value '{raw}' is not a rate.");
            }

            return Percentage.Zero;
        }

        return parsed.Value;
    }

    private static TradeInCreditPolicy ReadTradePolicy(JsonElement rules, bool strict, out Error? error)
    {
        error = null;

        switch (ReadString(rules, "tradeInCreditPolicy")?.ToLowerInvariant())
        {
            case "full":
                return TradeInCreditPolicy.Full;
            case "capped":
                return TradeInCreditPolicy.Capped;
            case "none":
                return TradeInCreditPolicy.None;
            default:
                if (strict)
                {
                    // Not a cosmetic default. Whether a trade reduces the taxable
                    // amount is worth thousands of dollars on a deal, and guessing
                    // "none" would overcharge every customer with a trade.
                    error = Error.Validation(
                        "rule_set.trade_policy_missing",
                        "This rule set is approved but does not say how a trade-in is credited " +
                        "against the taxable amount. That single field can be worth thousands " +
                        "on a deal; it cannot be defaulted.");
                }

                return TradeInCreditPolicy.None;
        }
    }

    private static RebateTaxTreatment ReadRebateTreatment(JsonElement rules, bool strict, out Error? error)
    {
        error = null;

        switch (ReadString(rules, "rebateTreatment")?.ToLowerInvariant())
        {
            case "taxable":
                return RebateTaxTreatment.Taxable;
            case "not_taxable":
            case "nottaxable":
                return RebateTaxTreatment.NotTaxable;
            default:
                if (strict)
                {
                    error = Error.Validation(
                        "rule_set.rebate_treatment_missing",
                        "This rule set is approved but does not say whether a rebate reduces " +
                        "the taxable amount.");
                }

                return RebateTaxTreatment.Taxable;
        }
    }

    private static List<FeeRule> ReadFees(JsonElement rules)
    {
        if (!rules.TryGetProperty("fees", out var fees) || fees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FeeRule>();

        foreach (var fee in fees.EnumerateArray())
        {
            var code = ReadString(fee, "code");

            if (code is null)
            {
                continue;
            }

            result.Add(new FeeRule(
                code,
                ReadBool(fee, "taxable", defaultValue: false),
                ReadBool(fee, "stateFee", defaultValue: false),
                ReadMoney(fee, "cap")));
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name, bool defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Reads a money value, which is always a string on the wire.
    /// </summary>
    /// <remarks>
    /// A JSON number would be parsed by a JavaScript client into an IEEE-754
    /// double, and a rule set edited through a browser would come back subtly
    /// wrong. A number here is therefore ignored rather than coerced — silently
    /// accepting it would defeat the convention everywhere else in the system.
    /// </remarks>
    private static Money? ReadMoney(JsonElement element, string name)
    {
        var raw = ReadString(element, name);

        if (raw is null || string.Equals(raw, Unverified, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parsed = Money.TryParse(raw);

        return parsed.IsSuccess ? parsed.Value : null;
    }
}
