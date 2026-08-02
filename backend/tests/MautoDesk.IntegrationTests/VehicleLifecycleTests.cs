using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The write paths the inventory screens drive.
/// </summary>
/// <remarks>
/// The UI offers exactly the status moves the server advertises in
/// <c>allowedTransitions</c>. That only holds if the field is real and tracks the
/// vehicle's current status, so it is asserted here rather than trusted — a stale
/// list means a salesperson picking a move that then fails.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class VehicleLifecycleTests
{
    private readonly ApiFixture _fixture;

    public VehicleLifecycleTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_vehicle_can_be_created_with_nothing_but_a_vin()
    {
        var client = await WriterAsync();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { vin = "1FTFW1ET5MFA48220", decodeVin = false });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The stock number is the server's to generate: two users adding a
        // vehicle at once must not race for the same one.
        body.RootElement.GetProperty("stockNumber").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("status").GetString().Should().Be("acquired");
    }

    [Fact]
    public async Task Allowed_transitions_track_the_current_status()
    {
        var client = await WriterAsync();
        var vehicle = await CreateAsync(client, "1FTFW1ET5MFA48221");

        var fromAcquired = await AllowedTransitionsAsync(client, vehicle);

        fromAcquired.Should().Contain("in_recon");
        fromAcquired.Should().Contain("available");

        // Nothing goes straight from acquired to sold, and the list must not
        // offer it — the domain would reject it.
        fromAcquired.Should().NotContain("sold");
        fromAcquired.Should().NotContain("delivered");

        var moved = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/status", UriKind.Relative),
            new { status = "available", reason = "recon finished" });

        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        var fromAvailable = await AllowedTransitionsAsync(client, vehicle);

        fromAvailable.Should().Contain("pending_sale");
        fromAvailable.Should().NotContain("delivered");
    }

    /// <summary>
    /// A move the list does not offer is refused by the server too.
    /// </summary>
    /// <remarks>
    /// The list is a convenience for the UI. This is the assertion that it is
    /// not the enforcement point.
    /// </remarks>
    [Fact]
    public async Task An_illegal_transition_is_refused_even_when_asked_for_directly()
    {
        var client = await WriterAsync();
        var vehicle = await CreateAsync(client, "1FTFW1ET5MFA48222");

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/status", UriKind.Relative),
            new { status = "delivered", reason = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("vehicle.status.invalid_transition");
    }

    /// <summary>
    /// Publishing an incomplete vehicle fails with something a dealer can act on.
    /// </summary>
    [Fact]
    public async Task Publishing_without_photos_explains_itself()
    {
        var client = await WriterAsync("inventory.vehicle.read", "inventory.vehicle.write", "inventory.publish");
        var vehicle = await CreateAsync(client, "1FTFW1ET5MFA48223");

        // A freshly acquired vehicle is refused on status first — publishing
        // something still in the back lot is the more common mistake.
        var tooEarly = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/publish", UriKind.Relative), new { });

        tooEarly.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tooEarly.Content.ReadAsStringAsync()).Should().Contain("vehicle.publish.not_available");

        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/status", UriKind.Relative),
            new { status = "available", reason = (string?)null });

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/publish", UriKind.Relative), new { });

        // 422, not 409: the vehicle is in a publishable *state*, it is simply
        // missing something the dealer can add.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // "needs a photo" is something a dealer can act on in the next minute,
        // which is the point of surfacing the API's own message on the screen.
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("photo", "the message is what the screen shows the user");
    }

    private async Task<HttpClient> WriterAsync(params string[] permissions)
    {
        var granted = permissions.Length > 0
            ? permissions
            : ["inventory.vehicle.read", "inventory.vehicle.write"];

        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, granted);
        return _fixture.AnonymousClient().WithToken(user.AccessToken);
    }

    private static async Task<string> CreateAsync(HttpClient client, string vin)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { vin, decodeVin = false });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<IReadOnlyList<string>> AllowedTransitionsAsync(HttpClient client, string vehicleId)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/vehicles/{vehicleId}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("allowedTransitions").EnumerateArray()
            .Select(status => status.GetString()!)];
    }
}
