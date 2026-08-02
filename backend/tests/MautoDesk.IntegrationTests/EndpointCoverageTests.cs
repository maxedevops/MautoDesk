using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Enumerates every route the API publishes and asserts the universal controls.
/// </summary>
/// <remarks>
/// <para>
/// docs/02-architecture.md §10 promised that <b>every</b> route is probed as
/// tenant B using tenant A's identifiers, and that "a new endpoint without a
/// probe fails the suite". Until Phase 9 that was hand-written per endpoint,
/// which is not the same thing at all: a developer adding an endpoint simply
/// would not have written the probe, and nothing would have noticed.
/// </para>
/// <para>
/// These tests read the generated OpenAPI document — the same one the client is
/// built from — so coverage tracks the API automatically.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class EndpointCoverageTests
{
    /// <summary>
    /// Routes that are anonymous by design.
    /// </summary>
    /// <remarks>
    /// Every entry is a deliberate decision that shows up in review. Adding one
    /// to silence a failure is how an endpoint quietly loses its authentication.
    /// </remarks>
    private static readonly HashSet<string> AnonymousByDesign = new(StringComparer.Ordinal)
    {
        "/api/v1/auth/login",
        "/api/v1/auth/mfa/verify",
        "/api/v1/auth/mfa/enrol",

        // Anonymous for the same reason as mfa/verify: the caller is halfway
        // through a login and holds a signed challenge token, not a bearer
        // token. The challenge is only issued after a correct password, so this
        // is still gated — MfaRecoveryTests asserts that a call without one is
        // refused.
        "/api/v1/auth/mfa/recovery",
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout",
    };

    private readonly ApiFixture _fixture;

    public EndpointCoverageTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>Every non-anonymous route rejects an unauthenticated caller.</summary>
    [Fact]
    public async Task Every_published_route_requires_authentication()
    {
        var client = _fixture.AnonymousClient();
        var failures = new List<string>();

        foreach (var (path, method) in await PublishedRoutesAsync())
        {
            if (AnonymousByDesign.Contains(path))
            {
                continue;
            }

            var response = await SendAsync(client, method, Concretize(path));

            // 401 is correct. 404 is acceptable for a path-parameterized route
            // ONLY if authentication ran first — but since we send no credential
            // at all, anything other than 401 means the route is reachable
            // unauthenticated.
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{method} {path} -> {(int)response.StatusCode} (expected 401)");
            }
        }

        failures.Should().BeEmpty(
            "every route not listed in AnonymousByDesign must reject an unauthenticated caller");
    }

    /// <summary>
    /// Every route taking a vehicle id refuses another tenant's id.
    /// </summary>
    /// <remarks>
    /// This is the promise from §10, now actually mechanical: a new endpoint
    /// with a <c>{vehicleId}</c> parameter is probed the moment it appears in
    /// the contract, without anyone remembering to write a test.
    /// </remarks>
    [Fact]
    public async Task Every_vehicle_route_refuses_another_tenants_identifier()
    {
        var client = _fixture.ClientFor(_fixture.UserB);
        var foreignId = _fixture.TenantAVehicleId;
        var probed = new List<string>();
        var failures = new List<string>();

        foreach (var (path, method) in await PublishedRoutesAsync())
        {
            if (!path.Contains("{vehicleId}", StringComparison.Ordinal))
            {
                continue;
            }

            var concrete = path.Replace("{vehicleId}", foreignId.ToString(), StringComparison.Ordinal);
            var response = await SendAsync(client, method, concrete);
            probed.Add($"{method} {path}");

            // 404, never 403: a 403 would confirm the record exists and turn any
            // id into an existence oracle for another dealership's data.
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                failures.Add($"{method} {path} -> {(int)response.StatusCode} (expected 404)");
            }
        }

        probed.Should().NotBeEmpty("the contract must expose vehicle routes to probe");
        failures.Should().BeEmpty(
            "a cross-tenant identifier must be indistinguishable from a missing one on every route");
    }

    /// <summary>Security headers are present on every response, including errors.</summary>
    [Fact]
    public async Task Security_headers_are_present_on_every_route()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var failures = new List<string>();

        string[] required =
        [
            "X-Content-Type-Options",
            "X-Frame-Options",
            "Content-Security-Policy",
            "Referrer-Policy",
            "Cache-Control",
        ];

        foreach (var (path, method) in await PublishedRoutesAsync())
        {
            var response = await SendAsync(client, method, Concretize(path));

            foreach (var header in required)
            {
                if (!response.Headers.Contains(header) && !response.Content.Headers.Contains(header))
                {
                    failures.Add($"{method} {path} is missing {header}");
                }
            }
        }

        failures.Should().BeEmpty("security headers apply to every response, success or failure");
    }

    /// <summary>
    /// No response leaks internals.
    /// </summary>
    /// <remarks>
    /// A stack trace, a SQL fragment, or a table name in an error body tells an
    /// attacker the shape of the system. Only the trace id crosses the boundary.
    /// </remarks>
    [Fact]
    public async Task No_response_leaks_stack_traces_or_sql()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var failures = new List<string>();

        string[] forbidden =
        [
            "Npgsql", "System.", "   at MautoDesk", "StackTrace",
            "select ", "insert into", "pg_", "Microsoft.EntityFrameworkCore",
        ];

        // Deliberately malformed and hostile inputs, plus the ordinary routes.
        string[] probes =
        [
            "/api/v1/vehicles/not-a-guid",
            "/api/v1/vehicles?page=-1&pageSize=99999",
            "/api/v1/vehicles?sort=';drop table inventory.vehicle;--",
            "/api/v1/vehicles?q=%27%20or%201%3D1--",
            "/api/v1/vin/NOTAVALIDVIN12345/decode",
            "/api/v1/vehicles/00000000-0000-0000-0000-000000000000",
        ];

        foreach (var probe in probes)
        {
            var response = await client.GetAsync(new Uri(probe, UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();

            foreach (var needle in forbidden)
            {
                if (body.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{probe} -> body contains '{needle}'");
                }
            }
        }

        failures.Should().BeEmpty("error responses must carry a trace id and nothing internal");
    }

    /// <summary>
    /// A SQL-injection attempt through a sortable field changes nothing.
    /// </summary>
    /// <remarks>
    /// The sort parameter is whitelisted rather than concatenated, so this
    /// should be inert — but "should be" is the reason to test it.
    /// </remarks>
    [Fact]
    public async Task Injection_attempts_through_query_parameters_are_inert()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var hostile = await client.GetAsync(new Uri(
            "/api/v1/vehicles?sort=stockNumber;drop%20table%20inventory.vehicle&q=%27%20OR%201%3D1--",
            UriKind.Relative));

        hostile.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown sort falls back, it does not error");

        // The table is still there and still tenant-scoped.
        var after = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /* --------------------------------------------------------------- helpers */

    private async Task<IReadOnlyList<(string Path, HttpMethod Method)>> PublishedRoutesAsync()
    {
        var client = _fixture.AnonymousClient();
        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        using var document = JsonDocument.Parse(json);
        var routes = new List<(string, HttpMethod)>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var method = operation.Name.ToUpperInvariant() switch
                {
                    "GET" => HttpMethod.Get,
                    "POST" => HttpMethod.Post,
                    "PUT" => HttpMethod.Put,
                    "PATCH" => HttpMethod.Patch,
                    "DELETE" => HttpMethod.Delete,
                    _ => null,
                };

                if (method is not null)
                {
                    routes.Add((path.Name, method));
                }
            }
        }

        return routes;
    }

    /// <summary>Substitutes plausible values for path parameters.</summary>
    private static string Concretize(string template) => template
        .Replace("{vehicleId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
        .Replace("{vin}", "1FTFW1ET5MFA48219", StringComparison.Ordinal);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            // An empty JSON object: enough to get past model binding so the
            // response reflects authentication and authorization rather than a
            // 400 for a missing body.
            request.Content = JsonContent.Create(new { });
        }

        return client.SendAsync(request);
    }
}
