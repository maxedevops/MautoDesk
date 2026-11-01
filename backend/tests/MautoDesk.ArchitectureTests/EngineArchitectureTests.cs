using System.Reflection;
using FluentAssertions;
using MautoDesk.Compliance.Contracts;
using MautoDesk.Compliance.Domain;
using MautoDesk.Sales.Contracts;
using MautoDesk.Sales.Domain;
using NetArchTest.Rules;
using Xunit;

namespace MautoDesk.ArchitectureTests;

/// <summary>
/// The properties that make the deal and regulatory engines trustworthy, as
/// failing tests rather than as claims in a comment.
/// </summary>
/// <remarks>
/// Both engines advertise themselves as pure and deterministic. That is a
/// property somebody can break in an afternoon with one convenient
/// <c>DateTime.UtcNow</c> or one <c>double</c>, and nothing would look wrong
/// until a number on a contract was.
/// </remarks>
public sealed class EngineArchitectureTests
{
    private static readonly Assembly SalesDomain = typeof(DealEngine).Assembly;
    private static readonly Assembly SalesContracts = typeof(SalesPermissions).Assembly;
    private static readonly Assembly ComplianceDomain = typeof(RuleEvaluator).Assembly;
    private static readonly Assembly ComplianceContracts = typeof(CompliancePermissions).Assembly;

    /// <summary>
    /// No binary floating point anywhere the money is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>double</c> cannot represent 0.1 exactly. In most software that is a
    /// rounding curiosity; here the affected values are a sales tax, a payment and
    /// a finance charge, printed on a document a consumer signs and a regulator may
    /// later read.
    /// </para>
    /// <para>
    /// This scans fields and method signatures as well as properties, because the
    /// tempting way to introduce one is <c>Math.Pow</c> in the middle of an
    /// amortization — a local, not a property.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("MautoDesk.Sales.Domain")]
    [InlineData("MautoDesk.Sales.Contracts")]
    [InlineData("MautoDesk.Compliance.Domain")]
    [InlineData("MautoDesk.Compliance.Contracts")]
    public void No_floating_point_reaches_the_engines(string assemblyName)
    {
        var assembly = AssemblyNamed(assemblyName);
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsFloatingPoint(property.PropertyType))
                {
                    offenders.Add($"{type.FullName}.{property.Name} (property)");
                }
            }

            foreach (var field in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsFloatingPoint(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} (field)");
                }
            }

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsFloatingPoint(method.ReturnType))
                {
                    offenders.Add($"{type.FullName}.{method.Name} (returns)");
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (IsFloatingPoint(parameter.ParameterType))
                    {
                        offenders.Add($"{type.FullName}.{method.Name}({parameter.Name})");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "money and rates are decimal; double cannot represent 0.1 and this code prices " +
            "contracts");
    }

    /// <summary>
    /// The engines read no clock.
    /// </summary>
    /// <remarks>
    /// Both take the governing date as an input, because the date a deal was
    /// written decides which rules apply. An engine that read the machine clock
    /// would price a January deal with July's rules the moment anyone recalculated
    /// it — and would do so silently.
    /// </remarks>
    [Theory]
    [InlineData("MautoDesk.Sales.Domain")]
    [InlineData("MautoDesk.Compliance.Domain")]
    public void The_engines_do_not_read_the_system_clock(string assemblyName)
    {
        var result = Types.InAssembly(AssemblyNamed(assemblyName))
            .ShouldNot()
            .HaveDependencyOn("System.DateTime")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "time is an input: the governing date arrives on the request, so that recomputing an " +
            "old deal applies the rules of its own day. Offenders: {0}",
            Describe(result));
    }

    /// <summary>
    /// The engines touch no infrastructure.
    /// </summary>
    /// <remarks>
    /// This is what lets the whole pipeline be tested without a container, and what
    /// makes a snapshot replayable from nothing but its stored input.
    /// </remarks>
    [Theory]
    [InlineData("MautoDesk.Sales.Domain")]
    [InlineData("MautoDesk.Compliance.Domain")]
    public void The_engines_are_free_of_io(string assemblyName)
    {
        var result = Types.InAssembly(AssemblyNamed(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.Logging",
                "Npgsql",
                "System.Net.Http",
                "System.Data",
                "System.IO")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "a pure engine has no database, no HTTP and no file system: {0}", Describe(result));
    }

    /// <summary>The two engines do not know about each other.</summary>
    /// <remarks>
    /// Deliberate separation, and the mechanism that makes adding a second state
    /// tractable. The deal engine computes money; the regulatory engine decides
    /// what the law permits; Deal Guardian orchestrates over both. A dependency
    /// either way would collapse that into one thing nobody can reason about.
    /// </remarks>
    [Fact]
    public void The_deal_engine_and_the_regulatory_engine_are_independent()
    {
        Types.InAssembly(SalesDomain)
            .ShouldNot().HaveDependencyOn("MautoDesk.Compliance")
            .GetResult().IsSuccessful.Should().BeTrue(
                "the calculator does not decide what the law permits");

        Types.InAssembly(ComplianceDomain)
            .ShouldNot().HaveDependencyOn("MautoDesk.Sales")
            .GetResult().IsSuccessful.Should().BeTrue(
                "the regulatory engine does not compute money");
    }

    /// <summary>A module's Contracts project does not export its internals.</summary>
    [Fact]
    public void Contracts_do_not_leak_engine_types()
    {
        Types.InAssembly(SalesContracts)
            .ShouldNot().HaveDependencyOn("MautoDesk.Sales.Domain")
            .GetResult().IsSuccessful.Should().BeTrue(
                "Contracts is the module's public API; exporting DealEngine's internals makes " +
                "every consumer depend on them");

        Types.InAssembly(ComplianceContracts)
            .ShouldNot().HaveDependencyOn("MautoDesk.Compliance.Domain")
            .GetResult().IsSuccessful.Should().BeTrue(
                "same rule, same reason");
    }

    /// <summary>
    /// Every line item kind the engine knows maps to a value the database accepts.
    /// </summary>
    /// <remarks>
    /// <c>sales.deal_line_item.kind</c> carries a check constraint listing exactly
    /// these values. The enum and the constraint drifting apart would surface as an
    /// insert failing in production on the one deal that used the new kind.
    /// </remarks>
    [Fact]
    public void Line_item_kinds_match_the_database_check_constraint()
    {
        // The V0004 constraint, transcribed. Changing it means changing both.
        var permitted = new[]
        {
            "fee", "product", "accessory", "discount", "rebate",
            "service_contract", "gap", "warranty", "tax_override",
        };

        var kinds = Enum.GetValues<LineItemKind>().Select(SnakeCase).ToArray();

        kinds.Should().BeEquivalentTo(
            permitted,
            "the enum and sales.deal_line_item's check constraint must name the same set");
    }

    /// <summary>Severities and statuses match their own check constraints.</summary>
    [Fact]
    public void Compliance_vocabularies_match_the_database()
    {
        Enum.GetValues<RuleSeverity>().Select(SnakeCase)
            .Should().BeEquivalentTo(["blocking", "warning", "review", "informational"]);

        Enum.GetValues<ComplianceStatus>().Select(SnakeCase)
            .Should().BeEquivalentTo(["pass", "warning", "block", "review"]);

        Enum.GetValues<RuleStatus>().Select(SnakeCase)
            .Should().BeEquivalentTo(["draft", "review", "approved", "scheduled", "active", "retired"]);
    }

    private static bool IsFloatingPoint(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(double) || underlying == typeof(float);
    }

    private static Assembly AssemblyNamed(string name) => name switch
    {
        "MautoDesk.Sales.Domain" => SalesDomain,
        "MautoDesk.Sales.Contracts" => SalesContracts,
        "MautoDesk.Compliance.Domain" => ComplianceDomain,
        "MautoDesk.Compliance.Contracts" => ComplianceContracts,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown assembly."),
    };

    /// <summary>Turns <c>ServiceContract</c> into <c>service_contract</c>.</summary>
    private static string SnakeCase<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString()!;
        var builder = new System.Text.StringBuilder();

        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "(none reported)"
            : string.Join(", ", result.FailingTypeNames);
}
