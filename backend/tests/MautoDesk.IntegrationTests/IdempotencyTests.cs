using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using MautoDesk.Inventory.Contracts;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The <c>Idempotency-Key</c> contract, exercised through the real host.
/// </summary>
/// <remarks>
/// These assertions are about a property that only matters under failure: a
/// client that retries because it never saw the first response. A unit test with
/// a stubbed store would prove the middleware calls the store; only this proves
/// that a retried <c>POST</c> does not create a second row in the database.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class IdempotencyTests
{
    private readonly ApiFixture _fixture;

    public IdempotencyTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_retried_request_replays_the_original_response_and_creates_one_row()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var key = Guid.NewGuid().ToString();
        var stockNumber = NewStockNumber();

        var first = await PostVehicleAsync(client, key, stockNumber);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await PostVehicleAsync(client, key, stockNumber);

        second.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "a replay returns what the original returned, including its status");

        var secondBody = await second.Content.ReadAsStringAsync();
        secondBody.Should().Be(firstBody, "the stored response is returned verbatim");

        second.Headers.TryGetValues("Idempotency-Replayed", out var flag).Should().BeTrue(
            "a client — and anyone reading a capture later — must be able to tell a replay from " +
            "a fresh execution");
        flag!.Should().ContainSingle().Which.Should().Be("true");

        // The assertion that actually matters. Without the middleware this is 2,
        // and the dealer has the same car in stock twice.
        var count = await CountVehiclesAsync(stockNumber);
        count.Should().Be(1, "the second request must not have created a second vehicle");
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_refused()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var key = Guid.NewGuid().ToString();

        var first = await PostVehicleAsync(client, key, NewStockNumber());
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostVehicleAsync(client, key, NewStockNumber());

        second.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "returning either result would be wrong: the first answer is not this request's, and " +
            "executing it would defeat the key");

        var problem = await second.Content.ReadAsStringAsync();
        problem.Should().Contain("idempotency.key_reused");
    }

    [Fact]
    public async Task A_completed_key_is_recorded_with_its_response()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var key = Guid.NewGuid().ToString();

        var response = await PostVehicleAsync(client, key, NewStockNumber());
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var (status, completed) = await ReadLedgerAsync(key);

        completed.Should().BeTrue("a finished request must be marked finished, or it replays as in-progress");
        status.Should().Be(201);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_deduplicated()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var stockNumber = NewStockNumber();

        var first = await PostVehicleAsync(client, key: null, stockNumber);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostVehicleAsync(client, key: null, stockNumber);

        // Not a replay — the duplicate stock number is caught by the domain,
        // which is the pre-existing behaviour. The point is that the middleware
        // is opt-in and did not quietly change it.
        second.StatusCode.Should().NotBe(
            HttpStatusCode.Created,
            "a duplicate stock number is still a conflict; idempotency is opt-in, not ambient");

        second.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
    }

    [Fact]
    public async Task A_key_belongs_to_one_tenant()
    {
        var keyText = Guid.NewGuid().ToString();

        var a = await PostVehicleAsync(_fixture.ClientFor(_fixture.UserA), keyText, NewStockNumber());
        a.StatusCode.Should().Be(HttpStatusCode.Created);

        // Tenant B presents the identical key. If the ledger were keyed on the
        // key alone, B would receive A's vehicle — the record of another
        // dealership's car, handed over by the deduplication layer.
        var b = await PostVehicleAsync(_fixture.ClientFor(_fixture.UserB), keyText, NewStockNumber());

        b.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the ledger is keyed (tenant_id, key, endpoint); one tenant's key is not another's");
        b.Headers.Contains("Idempotency-Replayed").Should().BeFalse(
            "this is a first execution for tenant B, not a replay of tenant A's response");
    }

    [Fact]
    public async Task The_same_key_on_a_different_endpoint_is_a_different_key()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var key = Guid.NewGuid().ToString();

        var created = await PostVehicleAsync(client, key, NewStockNumber());
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var vehicle = await created.Content.ReadFromJsonAsync<VehicleDto>();

        // A client that generates one key per user action and sends it to two
        // endpoints is doing something reasonable. Treating that as a collision
        // would break it.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/vehicles/{vehicle!.Id}/status", UriKind.Relative))
        {
            Content = JsonContent.Create(new { Status = "in_recon" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        var status = await client.SendAsync(request);

        status.Headers.Contains("Idempotency-Replayed").Should().BeFalse(
            "the endpoint is part of the ledger key, so this is a first execution");
        status.StatusCode.Should().NotBe(HttpStatusCode.Created);
    }

    private static string NewStockNumber() => "IT-IDEM-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<HttpResponseMessage> PostVehicleAsync(
        HttpClient client,
        string? key,
        string stockNumber)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/vehicles", UriKind.Relative))
        {
            // No VIN. inventory.vehicle has a unique index on (tenant_id, vin),
            // so reusing one across tests would make the *second* request fail
            // for a reason that has nothing to do with idempotency — and would
            // look exactly like the middleware working.
            Content = new StringContent(
                $$"""{"stockNumber":"{{stockNumber}}","mileage":46910}""",
                Encoding.UTF8),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    private static async Task<int> CountVehiclesAsync(string stockNumber)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select count(*)::int from inventory.vehicle where stock_number = @s",
            connection);
        command.Parameters.AddWithValue("s", stockNumber);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int? Status, bool Completed)> ReadLedgerAsync(string key)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select response_status, completed_at is not null from app.idempotency_key where key = @k",
            connection);
        command.Parameters.AddWithValue("k", key);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return (null, false);
        }

        var status = await reader.IsDBNullAsync(0) ? (int?)null : reader.GetInt32(0);
        return (status, reader.GetBoolean(1));
    }
}
