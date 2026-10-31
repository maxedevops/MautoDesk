using System.Globalization;

namespace MautoDesk.Compliance.Domain;

/// <summary>The comparisons a rule condition may make.</summary>
/// <remarks>
/// <para>
/// <b>This list is deliberately short, and adding to it is a decision.</b> The
/// temptation with a rule engine is to keep widening the vocabulary until it is a
/// programming language with none of a programming language's tooling. A rule
/// nobody can read is a rule nobody can audit, and an unauditable rule is worth
/// less than no rule at all — because it looks like compliance.
/// </para>
/// <para>
/// Everything here can be explained to a compliance officer in one sentence, which
/// is the bar for admission.
/// </para>
/// </remarks>
public enum ConditionOperator
{
    /// <summary>The field equals the value.</summary>
    Equals,

    /// <summary>The field does not equal the value.</summary>
    NotEquals,

    /// <summary>The field is numerically greater than the value.</summary>
    GreaterThan,

    /// <summary>The field is numerically greater than or equal to the value.</summary>
    GreaterThanOrEqual,

    /// <summary>The field is numerically less than the value.</summary>
    LessThan,

    /// <summary>The field is numerically less than or equal to the value.</summary>
    LessThanOrEqual,

    /// <summary>The field is one of a list of values.</summary>
    In,

    /// <summary>The field is not one of a list of values.</summary>
    NotIn,

    /// <summary>The field is present and not null.</summary>
    Exists,

    /// <summary>The field is absent or null.</summary>
    NotExists,
}

/// <summary>A node in a rule's condition tree.</summary>
/// <remarks>
/// Deserialized from the <c>definition</c> jsonb on
/// <c>compliance.regulatory_rule_version</c>, validated against a versioned JSON
/// Schema before it is stored. Three shapes only — a comparison, an <c>all</c>,
/// and an <c>any</c> — plus negation, which together cover every rule authored so
/// far without becoming a language.
/// </remarks>
public abstract record ConditionNode
{
    /// <summary>Evaluates this node against the facts of a deal.</summary>
    /// <param name="context">The flattened facts.</param>
    /// <param name="trace">Collects what was compared, for the evaluation detail.</param>
    public abstract bool Matches(RegulatoryContext context, ICollection<string> trace);
}

/// <summary>Every child must match.</summary>
/// <param name="Children">The conditions that must all hold.</param>
/// <remarks>An empty <c>all</c> matches, which is how a rule declares "always applies".</remarks>
public sealed record AllOf(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        // Deliberately not short-circuiting. The trace is the evidence a compliance
        // officer reads, and "we stopped looking after the first failure" makes it
        // impossible to tell which of three conditions actually held.
        var matched = true;

        foreach (var child in Children)
        {
            if (!child.Matches(context, trace))
            {
                matched = false;
            }
        }

        return matched;
    }
}

/// <summary>At least one child must match.</summary>
/// <param name="Children">The conditions, any of which may hold.</param>
/// <remarks>An empty <c>any</c> does not match — there is nothing to satisfy it.</remarks>
public sealed record AnyOf(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var matched = false;

        foreach (var child in Children)
        {
            if (child.Matches(context, trace))
            {
                matched = true;
            }
        }

        return matched;
    }
}

/// <summary>Inverts a child condition.</summary>
/// <param name="Child">The condition to negate.</param>
public sealed record Not(ConditionNode Child) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(RegulatoryContext context, ICollection<string> trace) =>
        !Child.Matches(context, trace);
}

/// <summary>Compares one fact against one value.</summary>
/// <param name="Field">A dotted path into the facts, e.g. <c>deal.isFinanced</c>.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">The value compared against, for the single-value operators.</param>
/// <param name="Values">The candidates, for <see cref="ConditionOperator.In"/> and its negation.</param>
public sealed record Comparison(
    string Field,
    ConditionOperator Operator,
    string? Value = null,
    IReadOnlyList<string>? Values = null) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trace);

        var actual = context.Get(Field);
        var result = Evaluate(actual);

        trace.Add(
            $"{Field} ({Describe(actual)}) {Operator} {Describe(Value ?? JoinValues())} => {result}");

        return result;
    }

    private bool Evaluate(string? actual) => Operator switch
    {
        ConditionOperator.Exists => actual is not null,
        ConditionOperator.NotExists => actual is null,

        // A missing fact does not satisfy a comparison. It is not false in the
        // sense of "we checked and it was not so" — it is "we do not know" — and
        // treating unknown as a match is how a rule silently stops firing when an
        // upstream field is renamed.
        _ when actual is null => false,

        ConditionOperator.Equals => Same(actual, Value),
        ConditionOperator.NotEquals => !Same(actual, Value),
        ConditionOperator.In => Values?.Any(candidate => Same(actual, candidate)) == true,
        ConditionOperator.NotIn => Values?.Any(candidate => Same(actual, candidate)) != true,
        _ => CompareNumeric(actual),
    };

    private bool CompareNumeric(string actual)
    {
        // decimal, not double. These comparisons decide whether a deal is blocked,
        // and several of them are against money — a fee cap, a down payment
        // minimum. Binary floating point has no business here.
        if (!decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) ||
            !decimal.TryParse(Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var right))
        {
            return false;
        }

        return Operator switch
        {
            ConditionOperator.GreaterThan => left > right,
            ConditionOperator.GreaterThanOrEqual => left >= right,
            ConditionOperator.LessThan => left < right,
            ConditionOperator.LessThanOrEqual => left <= right,
            _ => false,
        };
    }

    /// <summary>
    /// Compares two facts as equal.
    /// </summary>
    /// <remarks>
    /// Numeric when both sides parse as numbers, so <c>10</c> and <c>10.00</c>
    /// agree; otherwise an ordinal, case-insensitive string comparison, so
    /// <c>ELECTRONIC</c> and <c>electronic</c> agree. Culture-sensitive comparison
    /// is deliberately avoided — a rule must not evaluate differently on a server
    /// with a different locale.
    /// </remarks>
    private static bool Same(string actual, string? expected)
    {
        if (expected is null)
        {
            return false;
        }

        if (decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) &&
            decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var right))
        {
            return left == right;
        }

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private string JoinValues() => Values is null ? "-" : string.Join(", ", Values);

    private static string Describe(string? value) => value ?? "absent";
}
