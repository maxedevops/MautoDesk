using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The role × endpoint allow/deny matrix promised in docs/02-architecture.md §10.
/// </summary>
/// <remarks>
/// <para>
/// Table-driven, because authorization bugs are not usually a missing check —
/// they are a check that is present but wrong for one role out of seven, on one
/// endpoint out of ninety. Reading the code will not find that; enumerating the
/// combinations will.
/// </para>
/// <para>
/// Each case uses a real signed-in user whose permissions come from real role
/// grants and arrive in a real token, so this exercises the whole chain rather
/// than a mocked claim.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class AuthorizationMatrixTests
{
    private readonly ApiFixture _fixture;

    public AuthorizationMatrixTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Read access is gated on <c>inventory.vehicle.read</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// The negative cases matter more than the positive one. A user holding
    /// <em>some</em> inventory permission must still be refused if it is the
    /// wrong one — the common bug is checking "has any inventory permission".
    /// </remarks>
    [Theory]
    [InlineData("inventory.vehicle.read", HttpStatusCode.OK)]
    [InlineData("inventory.vehicle.write", HttpStatusCode.Forbidden)]
    [InlineData("inventory.photo.write", HttpStatusCode.Forbidden)]
    [InlineData("inventory.publish", HttpStatusCode.Forbidden)]
    [InlineData("crm.customer.read", HttpStatusCode.Forbidden)]
    public async Task Listing_inventory_requires_the_read_permission(
        string permission,
        HttpStatusCode expected)
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, permission);
        var client = _fixture.ClientFor(user);

        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(
            expected,
            "holding '{0}' must {1} inventory listing",
            permission,
            expected == HttpStatusCode.OK ? "allow" : "refuse");
    }

    [Theory]
    [InlineData("inventory.vehicle.write", HttpStatusCode.Created)]
    [InlineData("inventory.vehicle.read", HttpStatusCode.Forbidden)]
    [InlineData("inventory.price.write", HttpStatusCode.Forbidden)]
    public async Task Creating_a_vehicle_requires_the_write_permission(
        string permission,
        HttpStatusCode expected)
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, permission);
        var client = _fixture.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { stockNumber = "AZ-" + Guid.NewGuid().ToString("N")[..8] });

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("inventory.publish", HttpStatusCode.UnprocessableEntity)]
    [InlineData("inventory.vehicle.write", HttpStatusCode.Forbidden)]
    public async Task Publishing_requires_the_publish_permission(
        string permission,
        HttpStatusCode expected)
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, permission);
        var client = _fixture.ClientFor(user);

        var response = await client.PostAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}/publish", UriKind.Relative),
            content: null);

        // The permitted case reaches the domain and fails validation (no photos),
        // which is the correct 422 — it proves authorization passed and the rule
        // ran, rather than the request being refused at the gate.
        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("inventory.price.write", HttpStatusCode.OK)]
    [InlineData("inventory.vehicle.write", HttpStatusCode.Forbidden)]
    [InlineData("inventory.vehicle.read", HttpStatusCode.Forbidden)]
    public async Task Changing_a_price_requires_the_price_permission(
        string permission,
        HttpStatusCode expected)
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, permission);
        var client = _fixture.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}/price", UriKind.Relative),
            new { priceType = "list", newPrice = "31995.00", reason = "matrix test" });

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("inventory.vehicle.delete", HttpStatusCode.NoContent)]
    [InlineData("inventory.vehicle.write", HttpStatusCode.Forbidden)]
    public async Task Deleting_a_vehicle_requires_the_delete_permission(
        string permission,
        HttpStatusCode expected)
    {
        // A throwaway vehicle so the permitted case does not disturb shared fixtures.
        var owner = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "inventory.vehicle.write", "inventory.vehicle.read");

        var created = await _fixture.ClientFor(owner).PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { stockNumber = "DEL-" + Guid.NewGuid().ToString("N")[..8] });

        var vehicle = await created.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var vehicleId = vehicle!["id"].ToString();

        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, permission);
        var response = await _fixture.ClientFor(user)
            .DeleteAsync(new Uri($"/api/v1/vehicles/{vehicleId}", UriKind.Relative));

        response.StatusCode.Should().Be(expected);
    }

    /// <summary>
    /// A user with no permissions at all can authenticate and do nothing.
    /// </summary>
    /// <remarks>
    /// Deny by default. An account that exists but has been granted nothing must
    /// not inherit access from merely being signed in.
    /// </remarks>
    [Fact]
    public async Task A_user_with_no_permissions_can_sign_in_and_do_nothing()
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA);
        var client = _fixture.ClientFor(user);

        // Authentication works: /auth/me needs no permission.
        var me = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        // Everything else is refused.
        (await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative), new { stockNumber = "NOPE-1" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Permissions do not cross the tenant boundary.
    /// </summary>
    /// <remarks>
    /// A user with full inventory rights in tenant A holds exactly none of them
    /// in tenant B — the permission is scoped by the token's tenant claim, not
    /// global.
    /// </remarks>
    [Fact]
    public async Task Permissions_granted_in_one_tenant_do_not_apply_in_another()
    {
        var powerfulInA = await _fixture.CreateUserAsync(
            ApiFixture.TenantA,
            "inventory.vehicle.read", "inventory.vehicle.write", "inventory.vehicle.delete");

        var client = _fixture.ClientFor(powerfulInA);

        var response = await client.DeleteAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantBVehicleId}", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "delete rights in tenant A confer nothing in tenant B, and the response must not " +
            "reveal that the vehicle exists");
    }
}
