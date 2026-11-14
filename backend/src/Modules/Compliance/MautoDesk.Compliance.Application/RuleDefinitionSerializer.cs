using System.Text.Json;
using MautoDesk.Compliance.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Compliance.Application;

/// <summary>
/// Reads a rule's <c>definition</c> jsonb into a condition tree and its actions.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is the one in the DealerOS specification, deliberately small:
/// </para>
/// <code>
/// {
///   "conditions": {
///     "all": [
///       { "field": "deal.isFinanced", "operator": "equals", "value": "true" }
///     ]
///   },
///   "actions": [
///     { "require": { "code": "SECURITY_AGREEMENT", "name": "Security agreement" } },
///     { "deadline": { "code": "PRE_REGISTRATION", "days": 2, "businessDays": true } }
///   ]
/// }
/// </code>
/// <para>
/// <b>A definition that does not parse is an error, never a default.</b> There is
/// no lenient mode here, unlike the tax rule set: an unparseable condition tree
/// has no safe interpretation. Treating it as "matches nothing" would silently
/// disable a blocking rule, and treating it as "matches everything" would block
/// every deal. Both are worse than refusing to evaluate and saying so.
/// </para>
/// </remarks>
public static class RuleDefinitionSerializer
{
    /// <summary>What a definition holds once it has been read.</summary>
    /// <param name="Conditions">When the rule fires.</param>
    /// <param name="Actions">What firing produces.</param>
    public sealed record Definition(ConditionNode Conditions, IReadOnlyList<RuleAction> Actions);

    /// <summary>Parses a definition, or explains why it cannot be used.</summary>
    public static Result<Definition> Parse(string ruleCode, string definitionJson)
    {
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Error.Validation(
                "rule.definition_malformed",
                $"The definition for {ruleCode} is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation(
                "rule.definition_malformed",
                $"The definition for {ruleCode} must be a JSON object.");
        }

        ConditionNode conditions;

        if (root.TryGetProperty("conditions", out var conditionElement))
        {
            var parsed = ParseNode(ruleCode, conditionElement);

            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            conditions = parsed.Value!;
        }
        else
        {
            // No conditions means the rule always applies. An empty `all` is how
            // that is expressed, and it is a legitimate shape — "every retail deal
            // needs an odometer disclosure" has no precondition.
            conditions = new AllOf([]);
        }

        var actions = ParseActions(root);

        return actions.IsFailure
            ? actions.Error!
            : new Definition(conditions, actions.Value!);
    }

    private static Result<ConditionNode> ParseNode(string ruleCode, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Malformed(ruleCode, "a condition must be an object");
        }

        if (element.TryGetProperty("all", out var all))
        {
            return ParseGroup(ruleCode, all, children => new AllOf(children));
        }

        if (element.TryGetProperty("any", out var any))
        {
            return ParseGroup(ruleCode, any, children => new AnyOf(children));
        }

        if (element.TryGetProperty("not", out var not))
        {
            var inner = ParseNode(ruleCode, not);
            return inner.IsFailure ? inner.Error! : new Not(inner.Value!);
        }

        return ParseComparison(ruleCode, element);
    }

    private static Result<ConditionNode> ParseGroup(
        string ruleCode,
        JsonElement array,
        Func<List<ConditionNode>, ConditionNode> build)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return Malformed(ruleCode, "'all' and 'any' take an array of conditions");
        }

        var children = new List<ConditionNode>();

        foreach (var child in array.EnumerateArray())
        {
            var parsed = ParseNode(ruleCode, child);

            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            children.Add(parsed.Value!);
        }

        return build(children);
    }

    private static Result<ConditionNode> ParseComparison(string ruleCode, JsonElement element)
    {
        var field = ReadString(element, "field");

        if (field is null)
        {
            return Malformed(ruleCode, "a comparison needs a 'field'");
        }

        var operatorName = ReadString(element, "operator");

        if (operatorName is null || !TryParseOperator(operatorName, out var op))
        {
            return Malformed(ruleCode, $"'{operatorName ?? "(none)"}' is not a condition operator");
        }

        var value = ReadString(element, "value");
        List<string>? values = null;

        if (element.TryGetProperty("values", out var valuesElement)
            && valuesElement.ValueKind == JsonValueKind.Array)
        {
            values = [];

            foreach (var candidate in valuesElement.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String)
                {
                    values.Add(candidate.GetString()!);
                }
            }
        }

        if (op is ConditionOperator.In or ConditionOperator.NotIn && values is null)
        {
            return Malformed(ruleCode, $"'{operatorName}' needs a 'values' array");
        }

        if (op is not (ConditionOperator.Exists or ConditionOperator.NotExists
            or ConditionOperator.In or ConditionOperator.NotIn) && value is null)
        {
            return Malformed(ruleCode, $"'{operatorName}' needs a 'value'");
        }

        return new Comparison(field, op, value, values);
    }

    private static Result<IReadOnlyList<RuleAction>> ParseActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            // A rule with no actions is a bare assertion: firing is the finding.
            // Common for review-severity rules that exist to put a deal in front
            // of a human.
            return Result<IReadOnlyList<RuleAction>>.Success([]);
        }

        var result = new List<RuleAction>();

        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (action.TryGetProperty("require", out var require))
            {
                var code = ReadString(require, "code");

                if (code is not null)
                {
                    result.Add(new RequireAction(
                        code,
                        ReadString(require, "name") ?? code,
                        ReadString(require, "documentTemplateCode")));
                }
            }
            else if (action.TryGetProperty("prohibit", out var prohibit))
            {
                var message = ReadString(prohibit, "message");

                if (message is not null)
                {
                    result.Add(new ProhibitAction(message));
                }
            }
            else if (action.TryGetProperty("deadline", out var deadline))
            {
                var code = ReadString(deadline, "code");

                if (code is not null
                    && deadline.TryGetProperty("days", out var days)
                    && days.ValueKind == JsonValueKind.Number)
                {
                    result.Add(new DeadlineAction(
                        code,
                        days.GetInt32(),
                        !deadline.TryGetProperty("businessDays", out var business)
                        || business.ValueKind != JsonValueKind.False));
                }
            }
        }

        return Result<IReadOnlyList<RuleAction>>.Success(result);
    }

    private static bool TryParseOperator(string name, out ConditionOperator op) =>
        Enum.TryParse(
            name.Replace("_", string.Empty, StringComparison.Ordinal),
            ignoreCase: true,
            out op) && Enum.IsDefined(op);

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Error Malformed(string ruleCode, string problem) =>
        Error.Validation("rule.definition_malformed", $"The definition for {ruleCode} is invalid: {problem}.");
}
