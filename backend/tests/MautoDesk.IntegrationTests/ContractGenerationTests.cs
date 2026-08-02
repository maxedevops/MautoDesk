using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Makes the published API contract a build output rather than a hand-kept file.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0010 says the OpenAPI document generated from the API is the single
/// source of truth and that drift between backend and frontend types is a
/// failing build, not a runtime bug. Until now that was a promise; this is the
/// mechanism.
/// </para>
/// <para>
/// <b>Regenerating:</b> run the suite with <c>UPDATE_CONTRACT=1</c> and commit
/// the result.
/// </para>
/// <code>
/// UPDATE_CONTRACT=1 dotnet test backend/tests/MautoDesk.IntegrationTests
/// </code>
/// <para>
/// CI runs without that variable, so a developer who changes an endpoint and
/// forgets to regenerate gets a red build with a diff — rather than a frontend
/// that compiles against a contract the server no longer honours.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class ContractGenerationTests
{
    private static readonly JsonSerializerOptions NormalizedJson = new() { WriteIndented = true };

    private readonly ApiFixture _fixture;

    public ContractGenerationTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>The committed contract, relative to the repository root.</summary>
    private static string ContractPath => Path.Combine(RepositoryRoot(), "contracts", "openapi.json");

    [Fact]
    public async Task The_committed_contract_matches_what_the_api_actually_serves()
    {
        var generated = await FetchDocumentAsync();

        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ContractPath)!);
            await File.WriteAllTextAsync(ContractPath, generated);
            return;
        }

        File.Exists(ContractPath).Should().BeTrue(
            "the generated contract must be committed so the frontend can build a client from it. " +
            "Run the suite with UPDATE_CONTRACT=1 to create it.");

        var committed = Normalize(await File.ReadAllTextAsync(ContractPath));

        Normalize(generated).Should().Be(
            committed,
            "the API has changed but contracts/openapi.json was not regenerated. Run " +
            "`UPDATE_CONTRACT=1 dotnet test backend/tests/MautoDesk.IntegrationTests` and commit " +
            "the result — otherwise the frontend generates a client against a contract the server " +
            "no longer honours");
    }

    /// <summary>
    /// Guards the properties a generated client depends on.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte comparison catches drift but says nothing about whether the
    /// document is any good. These assertions pin the parts a client author — or
    /// a code generator — would break on.
    /// </remarks>
    [Fact]
    public async Task The_generated_contract_carries_the_metadata_a_client_needs()
    {
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var root = document.RootElement;

        root.GetProperty("info").GetProperty("title").GetString().Should().Be("MautoDesk API");
        root.GetProperty("openapi").GetString().Should().StartWith("3.");

        root.TryGetProperty("servers", out var servers).Should().BeTrue();
        servers.GetArrayLength().Should().BeGreaterThan(0);

        root.GetProperty("components")
            .GetProperty("securitySchemes")
            .TryGetProperty("bearerAuth", out _)
            .Should().BeTrue("a client must know how to authenticate");

        var paths = root.GetProperty("paths");
        paths.TryGetProperty("/api/v1/vehicles", out _).Should().BeTrue();
        paths.TryGetProperty("/api/v1/vehicles/{vehicleId}", out var vehicleById).Should().BeTrue();

        // The permission is emitted from the same constant the handler enforces,
        // so the contract cannot advertise a permission the server does not check.
        vehicleById.GetProperty("get").GetProperty("x-permission").GetString()
            .Should().Be("inventory.vehicle.read");

        // 404 must be documented on every path-parameterized operation, because
        // it is also the answer for a cross-tenant identifier.
        vehicleById.GetProperty("get").GetProperty("responses")
            .TryGetProperty("404", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Money_crosses_the_wire_as_a_string_never_as_a_number()
    {
        using var document = JsonDocument.Parse(await FetchDocumentAsync());

        var vehicleSchema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("VehicleDto")
            .GetProperty("properties");

        // If this ever becomes "number", a JavaScript client will round prices
        // through an IEEE-754 double and a contract can be off by a cent.
        vehicleSchema.GetProperty("listPrice").GetProperty("type").GetString()
            .Should().Be("string", "money is transported as a decimal string (Phase 4 §11)");
    }

    private async Task<string> FetchDocumentAsync()
    {
        var client = _fixture.AnonymousClient();
        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.IsSuccessStatusCode.Should().BeTrue(
            "the OpenAPI endpoint must be reachable for the contract to be generated");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Normalizes formatting so a whitespace change is not a drift.</summary>
    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, NormalizedJson);
    }

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repository root (no .git directory found above " +
                $"{AppContext.BaseDirectory}).");
    }
}
