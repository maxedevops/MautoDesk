using System.Reflection;
using FluentAssertions;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;
using NetArchTest.Rules;
using Xunit;

namespace MautoDesk.ArchitectureTests;

/// <summary>
/// The module boundaries from ADR-0001, as failing tests rather than review comments.
/// </summary>
/// <remarks>
/// A modular monolith becomes a ball of mud one reasonable-looking reference at
/// a time. Nobody ever decides to couple two modules; someone just needs a type
/// and the compiler lets them have it. These tests are the thing that says no,
/// at 3pm on a Friday, without a human having to notice in a diff.
/// </remarks>
public sealed class ArchitectureTests
{
    private static readonly Assembly SharedKernel = typeof(AggregateRoot).Assembly;
    private static readonly Assembly InventoryDomain = typeof(Vehicle).Assembly;
    private static readonly Assembly InventoryApplication = typeof(VehicleCommandHandler).Assembly;
    private static readonly Assembly InventoryContracts = typeof(VehicleDto).Assembly;
    private static readonly Assembly Infrastructure = typeof(MautoDeskDbContext).Assembly;

    /// <summary>
    /// The Domain layer depends on nothing but the SharedKernel.
    /// </summary>
    /// <remarks>
    /// No EF, no HTTP, no DI container, no JSON. The moment a domain type knows
    /// how it is persisted, the business rules stop being testable without a
    /// database and start being shaped by the ORM.
    /// </remarks>
    [Fact]
    public void Domain_does_not_depend_on_infrastructure_concerns()
    {
        var result = Types.InAssembly(InventoryDomain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions.DependencyInjection",
                "Npgsql",
                "System.Net.Http",
                "System.Data")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "the domain must stay persistence-ignorant, but these types reach into infrastructure: {0}",
            Describe(result));
    }

    /// <summary>The Application layer never references a concrete infrastructure type.</summary>
    [Fact]
    public void Application_does_not_depend_on_infrastructure()
    {
        var result = Types.InAssembly(InventoryApplication)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MautoDesk.Infrastructure",
                "MautoDesk.Inventory.Infrastructure",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "the application layer talks to ports, not adapters, but these types do not: {0}",
            Describe(result));
    }

    /// <summary>The SharedKernel stays framework-free.</summary>
    [Fact]
    public void SharedKernel_has_no_framework_dependencies()
    {
        var result = Types.InAssembly(SharedKernel)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions",
                "Npgsql")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "everything depends on the SharedKernel, so a dependency added here is added everywhere: {0}",
            Describe(result));
    }

    /// <summary>
    /// A module's public surface is its Contracts project and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the rule that makes extracting Inventory into its own service a
    /// mechanical job later: nothing outside the module holds a reference to its
    /// internals, so there is nothing to untangle first.
    /// </remarks>
    [Fact]
    public void Contracts_do_not_leak_domain_types()
    {
        var result = Types.InAssembly(InventoryContracts)
            .ShouldNot()
            .HaveDependencyOnAny("MautoDesk.Inventory.Domain", "MautoDesk.Inventory.Application")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Contracts is the module's public API; leaking a domain type into it exports the " +
            "internals to every consumer: {0}",
            Describe(result));
    }

    /// <summary>
    /// Nothing reads the machine clock except the clock.
    /// </summary>
    /// <remarks>
    /// Time is an input. A deal that computes differently at 23:59 than at 00:01
    /// must be testable at both, and "fails only around midnight" is not an
    /// acceptable property for software that prices contracts. Scanned via IL
    /// references because NetArchTest works at type granularity.
    /// </remarks>
    [Fact]
    public void Only_the_clock_reads_the_system_clock()
    {
        var offenders = new List<string>();

        foreach (var assembly in new[] { SharedKernel, InventoryDomain, InventoryApplication })
        {
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(IClock).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var body = method.GetMethodBody();
                    if (body is null)
                    {
                        continue;
                    }

                    // A direct call to DateTime.UtcNow / DateTimeOffset.UtcNow shows
                    // up as a reference to the property getter's declaring type. A
                    // full IL walk is overkill; checking that these assemblies do not
                    // name the accessors is enough to catch the mistake.
                    if (method.ToString()?.Contains("DateTime.UtcNow", StringComparison.Ordinal) == true)
                    {
                        offenders.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("time must be injected through IClock, not read statically");
    }

    /// <summary>
    /// No binary floating point anywhere near money.
    /// </summary>
    /// <remarks>
    /// <c>double</c> cannot represent 0.1 exactly. A cent of drift is a rounding
    /// curiosity in most software and a wrong number on a signed retail contract
    /// here. This test is the mechanical guarantee behind ADR-0008.
    /// </remarks>
    [Fact]
    public void No_floating_point_types_in_domain_or_contracts()
    {
        var offenders = new List<string>();

        foreach (var assembly in new[] { InventoryDomain, InventoryContracts })
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var propertyType = Nullable.GetUnderlyingType(property.PropertyType)
                        ?? property.PropertyType;

                    if (propertyType == typeof(double) || propertyType == typeof(float))
                    {
                        offenders.Add($"{type.FullName}.{property.Name} ({propertyType.Name})");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "money and measurements use decimal; double cannot represent 0.1 and this system " +
            "prints contracts");
    }

    /// <summary>Aggregates keep their invariants: no public setters.</summary>
    [Fact]
    public void Aggregates_do_not_expose_public_setters()
    {
        var offenders = new List<string>();

        foreach (var type in InventoryDomain.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(AggregateRoot))))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is { IsPublic: true })
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "state changes go through methods that enforce invariants; a public setter is a way " +
            "around every rule the aggregate exists to hold");
    }

    /// <summary>Infrastructure never depends on the API host.</summary>
    [Fact]
    public void Infrastructure_does_not_depend_on_the_host()
    {
        var result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn("MautoDesk.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "dependencies point inward; infrastructure knowing about the host inverts that: {0}",
            Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "(none reported)"
            : string.Join(", ", result.FailingTypeNames);
}
