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

        var stateRate = ReadRate(rules, "stateRate", strict, out var stateError);
        if (stateError is not null)
        {
            return stateError;
        }

        var localRate = ReadRate(rules, "localRate", strict, out var localError);
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
        };
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
