using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MautoDesk.Compliance.Domain;

/// <summary>
/// The facts a rule is evaluated against.
/// </summary>
/// <remarks>
/// <para>
/// A flat, sorted map of dotted paths to string values —
/// <c>deal.isFinanced</c> → <c>true</c>, <c>title.status</c> → <c>held</c>. Flat
/// rather than an object graph because a rule author writes a path, not a
/// traversal, and because a flat map has an obvious canonical form to hash.
/// </para>
/// <para>
/// <b>Everything is a string.</b> Not laziness — determinism. A value that arrives
/// as a string, is compared as a string or a <see cref="decimal"/>, and is hashed
/// as a string cannot change meaning because a serializer chose a different
/// numeric representation on a different runtime. The evaluator parses to
/// <see cref="decimal"/> where it needs to compare numerically, and never to
/// <see cref="double"/>.
/// </para>
/// <para>
/// The context is hashed into <c>compliance_run.input_hash</c>. The same facts,
/// under the same engine and rule pack version, must produce the same result hash
/// — and when they do not, that is a defect worth detecting rather than a
/// mystery.
/// </para>
/// </remarks>
public sealed class RegulatoryContext
{
    private readonly SortedDictionary<string, string> _facts;

    private RegulatoryContext(SortedDictionary<string, string> facts) => _facts = facts;

    /// <summary>The facts, in canonical order.</summary>
    public IReadOnlyDictionary<string, string> Facts => _facts;

    /// <summary>Requirement codes the deal has already satisfied.</summary>
    /// <remarks>
    /// A rule that requires a Buyer's Guide passes once the document exists. The
    /// evaluator needs to know which requirements are met, and this is where the
    /// application layer says so — from documents, tasks and inspections it has
    /// loaded, never from anything the client asserted.
    /// </remarks>
    public IReadOnlySet<string> SatisfiedRequirements { get; private init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts building a context.</summary>
    public static Builder Create() => new();

    /// <summary>Reads a fact, or null when it was never supplied.</summary>
    /// <remarks>
    /// Case-insensitive on the path, because <c>Deal.IsFinanced</c> and
    /// <c>deal.isFinanced</c> naming the same thing is a typo, not two facts.
    /// </remarks>
    public string? Get(string path) =>
        path is not null && _facts.TryGetValue(path, out var value) ? value : null;

    /// <summary>True when a requirement has been met.</summary>
    public bool IsSatisfied(string requirementCode) =>
        SatisfiedRequirements.Contains(requirementCode);

    /// <summary>
    /// The canonical form: one <c>path=value</c> per line, sorted.
    /// </summary>
    /// <remarks>
    /// Deliberately not JSON. A JSON serializer's key order, escaping and number
    /// formatting are all things that can change between library versions, and a
    /// hash that changes when a dependency is upgraded is a hash that proves
    /// nothing about the deal.
    /// </remarks>
    public string Canonical()
    {
        var builder = new StringBuilder();

        foreach (var (key, value) in _facts)
        {
            builder.Append(key).Append('=').Append(value).Append('\n');
        }

        builder.Append("satisfied=");
        builder.AppendJoin(',', SatisfiedRequirements.OrderBy(code => code, StringComparer.Ordinal));

        return builder.ToString();
    }

    /// <summary>SHA-256 of <see cref="Canonical"/>, for <c>compliance_run.input_hash</c>.</summary>
    public byte[] Hash() => SHA256.HashData(Encoding.UTF8.GetBytes(Canonical()));

    /// <summary>Assembles a context.</summary>
    /// <remarks>
    /// A builder rather than a dictionary literal so every value passes through a
    /// typed <c>With</c> overload. That is what keeps a <see cref="bool"/> from
    /// being written as <c>True</c> in one call site and <c>true</c> in another,
    /// which would make two identical deals hash differently.
    /// </remarks>
    public sealed class Builder
    {
        private readonly SortedDictionary<string, string> _facts =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _satisfied = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds a string fact. A null value is skipped, not stored as "null".</summary>
        public Builder With(string path, string? value)
        {
            if (value is not null)
            {
                _facts[path] = value;
            }

            return this;
        }

        /// <summary>Adds a boolean fact, always as <c>true</c> or <c>false</c>.</summary>
        public Builder With(string path, bool value)
        {
            _facts[path] = value ? "true" : "false";
            return this;
        }

        /// <summary>Adds a numeric fact in invariant culture.</summary>
        /// <remarks>
        /// <see cref="decimal"/> only. There is no <see cref="double"/> overload,
        /// and that is the point: a rule comparing a fee against a statutory cap
        /// must not be deciding on a binary approximation.
        /// </remarks>
        public Builder With(string path, decimal? value)
        {
            if (value is { } number)
            {
                _facts[path] = number.ToString(CultureInfo.InvariantCulture);
            }

            return this;
        }

        /// <summary>Adds an integer fact.</summary>
        public Builder With(string path, int? value)
        {
            if (value is { } number)
            {
                _facts[path] = number.ToString(CultureInfo.InvariantCulture);
            }

            return this;
        }

        /// <summary>Adds a date fact as <c>yyyy-MM-dd</c>.</summary>
        public Builder With(string path, DateOnly? value)
        {
            if (value is { } date)
            {
                _facts[path] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return this;
        }

        /// <summary>Records that a requirement has been met.</summary>
        public Builder Satisfying(string requirementCode)
        {
            _satisfied.Add(requirementCode);
            return this;
        }

        /// <summary>Records several satisfied requirements.</summary>
        public Builder Satisfying(IEnumerable<string> requirementCodes)
        {
            ArgumentNullException.ThrowIfNull(requirementCodes);

            foreach (var code in requirementCodes)
            {
                _satisfied.Add(code);
            }

            return this;
        }

        /// <summary>Produces the immutable context.</summary>
        public RegulatoryContext Build() => new(_facts)
        {
            SatisfiedRequirements = _satisfied,
        };
    }
}
