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

/// <summary>
/// Whether a condition held, did not hold, or could not be decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third value is the point.</b> A rule engine that answers only yes or no
/// has to answer "no" when a fact is simply absent, and "no" means the rule does
/// not fire, which means no finding, which reads as compliance. An incompletely
/// entered deal then becomes indistinguishable from a lawful one — the most
/// dangerous defect this system can have, because it fails silently and in the
/// direction of saying yes.
/// </para>
/// <para>
/// So absence is its own answer, and it propagates: see <see cref="AllOf"/>,
/// <see cref="AnyOf"/> and <see cref="Not"/>, which implement Kleene three-valued
/// logic. A rule whose conditions are <see cref="Unknown"/> produces a GRAY result
/// that a human has to clear.
/// </para>
/// </remarks>
public enum Match
{
    /// <summary>The condition is decided and does not hold.</summary>
    No,

    /// <summary>The condition is decided and holds.</summary>
    Yes,

    /// <summary>
    /// The condition cannot be decided, because a fact it needs is absent.
    /// </summary>
    /// <remarks>
    /// Not an error and not a failure. It is the honest answer to a question
    /// nobody supplied the input for, and the only safe thing to do with it is
    /// show it to someone.
    /// </remarks>
    Unknown,
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
    public abstract Match Matches(RegulatoryContext context, ICollection<string> trace);

    /// <summary>
    /// Every fact path this node reads, whether or not it is present.
    /// </summary>
    /// <remarks>
    /// Used to tell a human <em>which</em> fact is missing. "Something is unknown"
    /// is not actionable; "the odometer reading is missing" is.
    /// </remarks>
    public abstract IEnumerable<string> Fields();

    /// <summary>The fact paths this node needs and the context does not have.</summary>
    public IEnumerable<string> MissingFields(RegulatoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Fields().Where(field => context.Get(field) is null).Distinct(StringComparer.Ordinal);
    }
}

/// <summary>Every child must match.</summary>
/// <param name="Children">The conditions that must all hold.</param>
/// <remarks>An empty <c>all</c> matches, which is how a rule declares "always applies".</remarks>
public sealed record AllOf(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    /// <remarks>
    /// A decided <see cref="Match.No"/> wins over an <see cref="Match.Unknown"/>:
    /// if one child definitely does not hold, the conjunction definitely does not
    /// hold, and no missing fact elsewhere can change that. This matters — without
    /// it, every rule scoped to retail deals would go GRAY on a wholesale deal
    /// merely because some retail-only fact was absent.
    /// </remarks>
    public override Match Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        // Deliberately not short-circuiting. The trace is the evidence a compliance
        // officer reads, and "we stopped looking after the first failure" makes it
        // impossible to tell which of three conditions actually held.
        var unknown = false;
        var decidedNo = false;

        foreach (var child in Children)
        {
            switch (child.Matches(context, trace))
            {
                case Match.No:
                    decidedNo = true;
                    break;
                case Match.Unknown:
                    unknown = true;
                    break;
            }
        }

        if (decidedNo)
        {
            return Match.No;
        }

        return unknown ? Match.Unknown : Match.Yes;
    }

    /// <inheritdoc />
    public override IEnumerable<string> Fields() => Children.SelectMany(child => child.Fields());
}

/// <summary>At least one child must match.</summary>
/// <param name="Children">The conditions, any of which may hold.</param>
/// <remarks>An empty <c>any</c> does not match — there is nothing to satisfy it.</remarks>
public sealed record AnyOf(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    /// <remarks>
    /// The mirror of <see cref="AllOf"/>: a decided <see cref="Match.Yes"/> wins
    /// over an <see cref="Match.Unknown"/>, because one child that definitely holds
    /// satisfies the disjunction whatever is missing elsewhere.
    /// </remarks>
    public override Match Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var unknown = false;
        var decidedYes = false;

        foreach (var child in Children)
        {
            switch (child.Matches(context, trace))
            {
                case Match.Yes:
                    decidedYes = true;
                    break;
                case Match.Unknown:
                    unknown = true;
                    break;
            }
        }

        if (decidedYes)
        {
            return Match.Yes;
        }

        return unknown ? Match.Unknown : Match.No;
    }

    /// <inheritdoc />
    public override IEnumerable<string> Fields() => Children.SelectMany(child => child.Fields());
}

/// <summary>Inverts a child condition.</summary>
/// <param name="Child">The condition to negate.</param>
public sealed record Not(ConditionNode Child) : ConditionNode
{
    /// <inheritdoc />
    /// <remarks>
    /// Negating an unknown leaves it unknown. "We do not know whether X" negates to
    /// "we do not know whether not-X", not to "not-X".
    /// </remarks>
    public override Match Matches(RegulatoryContext context, ICollection<string> trace) =>
        Child.Matches(context, trace) switch
        {
            Match.Yes => Match.No,
            Match.No => Match.Yes,
            _ => Match.Unknown,
        };

    /// <inheritdoc />
    public override IEnumerable<string> Fields() => Child.Fields();
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
    public override Match Matches(RegulatoryContext context, ICollection<string> trace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trace);

        var actual = context.Get(Field);
        var result = Evaluate(actual);

        trace.Add(
            $"{Field} ({Describe(actual)}) {Operator} {Describe(Value ?? JoinValues())} => {result}");

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ConditionOperator.Exists"/> and its negation are excluded: they
    /// are the two operators whose entire purpose is to ask about absence, so for
    /// them an absent fact is the answer rather than a gap in the input.
    /// </remarks>
    public override IEnumerable<string> Fields() =>
        Operator is ConditionOperator.Exists or ConditionOperator.NotExists
            ? []
            : [Field];

    private Match Evaluate(string? actual) => Operator switch
    {
        ConditionOperator.Exists => Decided(actual is not null),
        ConditionOperator.NotExists => Decided(actual is null),

        // A missing fact does not satisfy a comparison, and it does not fail one
        // either. It is not false in the sense of "we checked and it was not so" —
        // it is "we do not know", and the two must not collapse into one answer.
        // Returning false here is what made a rule over an uncaptured fact stop
        // firing, produce no finding, and read as compliance.
        _ when actual is null => Match.Unknown,

        ConditionOperator.Equals => Decided(Same(actual, Value)),
        ConditionOperator.NotEquals => Negate(Same(actual, Value)),
        ConditionOperator.In => Decided(Values?.Any(candidate => Same(actual, candidate)) == true),
        ConditionOperator.NotIn => Negate(Values?.Any(candidate => Same(actual, candidate)) == true),
        _ => CompareNumeric(actual),
    };

    private Match CompareNumeric(string actual)
    {
        // decimal, not double. These comparisons decide whether a deal is blocked,
        // and several of them are against money — a fee cap, a down payment
        // minimum. Binary floating point has no business here.
        var actualIsNumeric = decimal.TryParse(
            actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var left);
        var expectedIsNumeric = decimal.TryParse(
            Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var right);

        // A fact that is present but not a number is a different problem from an
        // absent one, and still not something to answer "no" to. Something upstream
        // is putting "n/a" or "" where a figure belongs, and a human needs to see
        // that rather than have the rule quietly decline to fire.
        if (!actualIsNumeric || !expectedIsNumeric)
        {
            return Match.Unknown;
        }

        return Operator switch
        {
            ConditionOperator.GreaterThan => Decided(left > right),
            ConditionOperator.GreaterThanOrEqual => Decided(left >= right),
            ConditionOperator.LessThan => Decided(left < right),
            ConditionOperator.LessThanOrEqual => Decided(left <= right),
            _ => Match.Unknown,
        };
    }

    private static Match Decided(bool held) => held ? Match.Yes : Match.No;

    private static Match Negate(bool held) => held ? Match.No : Match.Yes;

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
