using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Proves tenant isolation holds through the entire application stack.
/// </summary>
/// <remarks>
/// <c>db/tests/isolation_probe.sql</c> proves the database enforces isolation.
/// These tests prove the <em>application</em> does not undo it — that the
/// connection interceptor sets the right tenant, that pooling does not leak one
/// request's scope into the next, and that a cross-tenant identifier surfaces as
/// 404 rather than as data or as a 403 that confirms the record exists.
///
/// A cross-tenant leak is existential for a multi-tenant SaaS. This file is the
/// regression guard.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class TenantIsolationTests
{
    private readonly ApiFixture _fixture;

    public TenantIsolationTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_A_vehicle_by_its_primary_key()
    {
        var client = _fixture.ClientFor(_fixture.UserB);

        var response = await client.GetAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a cross-tenant id must be indistinguishable from a missing one — 403 would confirm " +
            "the record exists and turn any id into an existence oracle");
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_vehicles_in_a_list()
    {
        var client = _fixture.ClientFor(_fixture.UserB);

        var page = await client.GetFromJsonAsync<PagedResult<VehicleSummaryDto>>(
            new Uri("/api/v1/vehicles?pageSize=100", UriKind.Relative));

        var ownedByA = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantA);
        var ownedByB = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantB);

        page.Should().NotBeNull();
        page!.Items.Should().NotBeEmpty();
        page.Items.Should().OnlyContain(
            v => ownedByB.Contains(v.Id),
            "every row returned must belong to the requesting tenant");
        page.Items.Should().NotContain(
            v => ownedByA.Contains(v.Id),
            "no row belonging to another tenant may appear");
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_tenant_A_vehicle()
    {
        var client = _fixture.ClientFor(_fixture.UserB);

        var response = await client.DeleteAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And the row is genuinely untouched, not merely reported as missing.
        var stillThere = await VehicleExistsAsync(_fixture.TenantAVehicleId);
        stillThere.Should().BeTrue("the delete must not have landed");
    }

    [Fact]
    public async Task Tenant_B_cannot_change_the_status_of_tenant_A_vehicle()
    {
        var client = _fixture.ClientFor(_fixture.UserB);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}/status", UriKind.Relative),
            new { status = "sold" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The failure mode connection pooling makes possible.
    /// </summary>
    /// <remarks>
    /// Connection #7 handling tenant A's request must not carry
    /// <c>app.tenant_id = A</c> when the pool later hands it to tenant B. This
    /// alternates tenants many times specifically to force reuse; a single
    /// request each would pass even with a broken interceptor.
    /// </remarks>
    [Fact]
    public async Task Alternating_tenants_across_pooled_connections_never_leaks_scope()
    {
        var clientA = _fixture.ClientFor(_fixture.UserA);
        var clientB = _fixture.ClientFor(_fixture.UserB);

        var ownedByA = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantA);
        var ownedByB = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantB);

        for (var i = 0; i < 12; i++)
        {
            var pageA = await clientA.GetFromJsonAsync<PagedResult<VehicleSummaryDto>>(
                new Uri("/api/v1/vehicles?pageSize=100", UriKind.Relative));

            pageA!.Items.Should().NotContain(
                v => ownedByB.Contains(v.Id),
                "iteration {0}: tenant A must never see tenant B's inventory", i);

            var pageB = await clientB.GetFromJsonAsync<PagedResult<VehicleSummaryDto>>(
                new Uri("/api/v1/vehicles?pageSize=100", UriKind.Relative));

            pageB!.Items.Should().NotContain(
                v => ownedByA.Contains(v.Id),
                "iteration {0}: tenant B must never see tenant A's inventory", i);
        }
    }

    [Fact]
    public async Task Concurrent_requests_from_two_tenants_do_not_cross_over()
    {
        var clientA = _fixture.ClientFor(_fixture.UserA);
        var clientB = _fixture.ClientFor(_fixture.UserB);

        var work = new List<Task<PagedResult<VehicleSummaryDto>?>>();

        for (var i = 0; i < 10; i++)
        {
            work.Add(clientA.GetFromJsonAsync<PagedResult<VehicleSummaryDto>>(
                new Uri("/api/v1/vehicles?pageSize=100", UriKind.Relative)));
            work.Add(clientB.GetFromJsonAsync<PagedResult<VehicleSummaryDto>>(
                new Uri("/api/v1/vehicles?pageSize=100", UriKind.Relative)));
        }

        var pages = await Task.WhenAll(work);

        var ownedByA = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantA);
        var ownedByB = await ApiFixture.VehicleIdsOwnedByAsync(ApiFixture.TenantB);

        // Every page belongs entirely to one tenant. A mixed page would mean the
        // scope was read from a connection that had been reassigned mid-flight.
        foreach (var page in pages)
        {
            var touchesA = page!.Items.Any(v => ownedByA.Contains(v.Id));
            var touchesB = page.Items.Any(v => ownedByB.Contains(v.Id));

            (touchesA && touchesB).Should().BeFalse(
                "a single response must never mix two tenants' vehicles");
        }
    }

    /// <summary>
    /// No tenant means no data — never all data.
    /// </summary>
    /// <remarks>
    /// <c>app.current_tenant_id()</c> returns NULL when the session variable is
    /// unset, so every RLS predicate evaluates to NULL and denies. This asserts
    /// the application fails closed too rather than reading with an empty scope.
    /// </remarks>
    [Fact]
    public async Task An_unauthenticated_request_is_refused_rather_than_served_everything()
    {
        var client = _fixture.AnonymousClient();

        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "with real authentication in place, no credential is 401 rather than 403");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("IT-A").And.NotContain("IT-B");
    }

    /// <summary>
    /// The read path respects permissions independently of tenancy.
    /// </summary>
    [Fact]
    public async Task A_user_without_the_read_permission_is_refused()
    {
        // A real signed-in user who simply lacks inventory.vehicle.read. The
        // permission comes from their token, which comes from their role grants —
        // so this exercises the whole chain, not a header override.
        var restricted = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.photo.write");
        var client = _fixture.ClientFor(restricted);

        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "authenticated but unauthorized is 403; unauthenticated is 401");
    }

    private static async Task<bool> VehicleExistsAsync(Guid vehicleId)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select count(*) from inventory.vehicle where id = @id and deleted_at is null",
            connection);
        command.Parameters.AddWithValue("id", vehicleId);

        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }
}

/// <summary>The vertical slice working end to end.</summary>
[Collection(nameof(ApiCollection))]
public sealed class VehicleWorkflowTests
{
    private readonly ApiFixture _fixture;

    public VehicleWorkflowTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Creating_a_vehicle_decodes_the_vin_and_writes_an_outbox_message()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var stockNumber = "IT-A-" + Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new CreateVehicleCommand
            {
                StockNumber = stockNumber,
                Vin = "3VWDX7AJ5DM301234",
                Mileage = 46_910,
                ListPrice = "38450.00",
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<VehicleDto>();
        created.Should().NotBeNull();
        created!.StockNumber.Should().Be(stockNumber);
        created.Make.Should().Be("Ford", "the decoder fills identity fields that were left empty");
        created.ListPrice.Should().Be("38450.00", "money is a decimal string, never a JSON number");
        created.Status.Should().Be("acquired");

        // The outbox row must exist, written in the same transaction as the
        // vehicle. This is what makes "enter data once, it flows everywhere"
        // survive a crash immediately after the save.
        var outboxCount = await CountOutboxAsync(created.Id, "inventory.vehicle.created");
        outboxCount.Should().Be(1, "the domain event is committed with the state change, not after it");
    }

    [Fact]
    public async Task A_vehicle_saves_with_nothing_but_a_stock_number()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new CreateVehicleCommand { StockNumber = "IT-A-" + Guid.NewGuid().ToString("N")[..8] });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "a salesperson on a lot has a stock number and nothing else; refusing the save is how " +
            "a DMS loses to a notebook");

        var created = await response.Content.ReadFromJsonAsync<VehicleDto>();
        created!.Readiness.IsMissingPrice().Should().BeTrue();
        created.Readiness.Satisfied.Should().BeLessThan(created.Readiness.Total);
    }

    [Fact]
    public async Task A_duplicate_stock_number_is_a_conflict_not_a_crash()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var stockNumber = "IT-A-" + Guid.NewGuid().ToString("N")[..8];
        var body = new CreateVehicleCommand { StockNumber = stockNumber };

        await client.PostAsJsonAsync(new Uri("/api/v1/vehicles", UriKind.Relative), body);
        var second = await client.PostAsJsonAsync(new Uri("/api/v1/vehicles", UriKind.Relative), body);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadAsStringAsync();
        problem.Should().Contain("vehicle.stock_number.duplicate");
        problem.Should().Contain("traceId", "every response carries a trace id for support");
    }

    [Fact]
    public async Task Two_tenants_can_use_the_same_stock_number()
    {
        var stockNumber = "SHARED-" + Guid.NewGuid().ToString("N")[..6];

        var a = await _fixture.ClientFor(_fixture.UserA)
            .PostAsJsonAsync(
                new Uri("/api/v1/vehicles", UriKind.Relative),
                new CreateVehicleCommand { StockNumber = stockNumber });

        var b = await _fixture.ClientFor(_fixture.UserB)
            .PostAsJsonAsync(
                new Uri("/api/v1/vehicles", UriKind.Relative),
                new CreateVehicleCommand { StockNumber = stockNumber });

        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "stock numbers are unique per tenant, not globally — two dealers both having an A-100 " +
            "is completely ordinary");
    }

    [Fact]
    public async Task Publishing_without_photos_is_refused_with_a_useful_reason()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.PostAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}/publish", UriKind.Relative),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("vehicle.publish.no_photos");
        problem.Should().Contain("skipped by shoppers", "an error should say why, not just refuse");
    }

    [Fact]
    public async Task An_invalid_vin_is_rejected_with_an_explanation()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new CreateVehicleCommand
            {
                StockNumber = "IT-A-" + Guid.NewGuid().ToString("N")[..8],
                Vin = "IFTFW1ET5MFA48219",  // leading I, which no VIN contains
            });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("I, O or Q");
    }

    [Fact]
    public async Task Security_headers_are_present_on_every_response()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Content-Security-Policy").Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_health_check_confirms_we_are_not_connected_as_a_superuser()
    {
        var response = await _fixture.AnonymousClient()
            .GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "readiness fails deliberately if the app is connected as a role that can bypass RLS");
    }

    private static async Task<int> CountOutboxAsync(Guid vehicleId, string eventType)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select count(*)::int from app.outbox_message
             where event_type = @type and payload->>'vehicleId' = @vehicleId
            """,
            connection);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("vehicleId", vehicleId.ToString());

        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }
}

internal static class ReadinessExtensions
{
    public static bool IsMissingPrice(this PublishReadinessDto readiness) =>
        readiness.Missing.Contains("Price", StringComparer.Ordinal);
}
