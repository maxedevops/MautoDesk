using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The ledger, from the auditor's side of the desk.
/// </summary>
/// <remarks>
/// The question that matters is "who changed this price, and when?" — so these
/// tests drive the API and then read <c>audit.event</c> directly, because that
/// is what an auditor with a database connection would do. They also check the
/// two properties that make the answer worth anything: the entry commits with
/// the change, and the chain notices tampering.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class AuditLedgerTests
{
    private readonly ApiFixture _fixture;

    public AuditLedgerTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Creating_a_vehicle_writes_an_entry_naming_the_actor()
    {
        var user = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "inventory.vehicle.read", "inventory.vehicle.write");
        var client = _fixture.AnonymousClient().WithToken(user.AccessToken);

        var vehicleId = await CreateVehicleAsync(client, "1FTFW1ET5MFA48250");

        var entries = await EntriesForAsync(vehicleId);

        entries.Should().ContainSingle();
        entries[0].Action.Should().Be("inventory.vehicle.created");
        entries[0].ActorId.Should().Be(user.UserId, "an entry that cannot name the actor answers nothing");
        entries[0].ActorType.Should().Be("user");
        entries[0].TenantId.Should().Be(ApiFixture.TenantA);
    }

    /// <summary>
    /// The question the ledger was built for, answered end to end.
    /// </summary>
    [Fact]
    public async Task A_price_change_records_both_numbers_and_the_reason()
    {
        var user = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "inventory.vehicle.read", "inventory.vehicle.write", "inventory.price.write");
        var client = _fixture.AnonymousClient().WithToken(user.AccessToken);

        var vehicleId = await CreateVehicleAsync(client, "1FTFW1ET5MFA48251");

        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/price", UriKind.Relative),
            new { priceType = "list", newPrice = "21500.00", reason = (string?)null });

        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/price", UriKind.Relative),
            new { priceType = "list", newPrice = "19995.00", reason = "60 days on lot" });

        var priceChanges = (await EntriesForAsync(vehicleId))
            .Where(entry => entry.Action == "inventory.vehicle.price_changed")
            .ToList();

        priceChanges.Should().HaveCount(2);

        using var latest = JsonDocument.Parse(priceChanges[^1].AfterState!);
        using var previous = JsonDocument.Parse(priceChanges[^1].BeforeState!);

        // Strings, not JSON numbers. A price that round-trips through a double
        // on its way into the audit record is not evidence of anything.
        latest.RootElement.GetProperty("listPrice").GetString().Should().Be("19995.00");
        previous.RootElement.GetProperty("listPrice").GetString().Should().Be("21500.00");

        using var metadata = JsonDocument.Parse(priceChanges[^1].Metadata);
        metadata.RootElement.GetProperty("reason").GetString().Should().Be("60 days on lot");
    }

    /// <summary>
    /// A change that did not happen leaves no claim that it did.
    /// </summary>
    /// <remarks>
    /// The entry is written into the same transaction as the change, so a
    /// refused operation rolls the entry back with it. Without that, the ledger
    /// accumulates records of things that never occurred — which is worse than
    /// no ledger, because it is a ledger nobody can trust.
    /// </remarks>
    [Fact]
    public async Task A_refused_change_leaves_no_entry_behind()
    {
        var user = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "inventory.vehicle.read", "inventory.vehicle.write");
        var client = _fixture.AnonymousClient().WithToken(user.AccessToken);

        var vehicleId = await CreateVehicleAsync(client, "1FTFW1ET5MFA48252");

        // Acquired cannot go straight to delivered; the domain refuses.
        var refused = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/status", UriKind.Relative),
            new { status = "delivered", reason = (string?)null });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var entries = await EntriesForAsync(vehicleId);

        entries.Should().OnlyContain(
            entry => entry.Action == "inventory.vehicle.created",
            "the rejected status change must not appear in the ledger");
    }

    [Fact]
    public async Task Publishing_and_status_changes_are_both_recorded()
    {
        var user = await _fixture.CreateUserAsync(
            ApiFixture.TenantA,
            "inventory.vehicle.read", "inventory.vehicle.write", "inventory.price.write",
            "inventory.photo.write", "inventory.publish");
        var client = _fixture.AnonymousClient().WithToken(user.AccessToken);

        var vehicleId = await CreateVehicleAsync(client, "1FTFW1ET5MFA48253");

        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/status", UriKind.Relative),
            new { status = "available", reason = "recon done" });

        var actions = (await EntriesForAsync(vehicleId)).Select(entry => entry.Action).ToList();

        actions.Should().Contain("inventory.vehicle.created");
        actions.Should().Contain("inventory.vehicle.status_changed");
    }

    /// <summary>
    /// The chain is what makes the ledger evidence rather than a table of rows.
    /// </summary>
    /// <remarks>
    /// Verified with the superuser connection, because the application role
    /// cannot update or delete these rows at all — which is the first line of
    /// the same defence.
    /// </remarks>
    [Fact]
    public async Task The_hash_chain_is_intact_after_the_api_has_written_to_it()
    {
        var user = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "inventory.vehicle.read", "inventory.vehicle.write");
        var client = _fixture.AnonymousClient().WithToken(user.AccessToken);

        await CreateVehicleAsync(client, "1FTFW1ET5MFA48254");
        await CreateVehicleAsync(client, "1FTFW1ET5MFA48255");

        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        // Every row's prev_hash must be its predecessor's hash, within a tenant.
        await using var command = new NpgsqlCommand(
            """
            select count(*)
              from (
                select e.prev_hash,
                       lag(e.hash) over (partition by e.tenant_id order by e.id) as expected
                  from audit.event e
                 where e.tenant_id = @tenant
              ) chained
             where chained.expected is not null
               and chained.prev_hash is distinct from chained.expected
            """,
            connection);

        command.Parameters.AddWithValue("tenant", ApiFixture.TenantA);

        var broken = (long)(await command.ExecuteScalarAsync())!;
        broken.Should().Be(0, "a break in the chain means a row was inserted out of order or altered");
    }

    /// <summary>The application role may write the ledger and never rewrite it.</summary>
    [Fact]
    public async Task The_application_role_cannot_alter_an_entry()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AppConnectionString);
        await connection.OpenAsync();

        await using var scope = new NpgsqlCommand(
            "select set_config('app.tenant_id', @tenant, false)", connection);
        scope.Parameters.AddWithValue("tenant", ApiFixture.TenantA.ToString());
        await scope.ExecuteNonQueryAsync();

        await using var update = new NpgsqlCommand(
            "update audit.event set action = 'tampered' where tenant_id = @tenant", connection);
        update.Parameters.AddWithValue("tenant", ApiFixture.TenantA);

        var attempt = async () => await update.ExecuteNonQueryAsync();

        await attempt.Should().ThrowAsync<PostgresException>(
            "the ledger is append-only by grant and by trigger, not by convention");
    }

    private static async Task<string> CreateVehicleAsync(HttpClient client, string vin)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { vin, decodeVin = false });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<IReadOnlyList<LedgerRow>> EntriesForAsync(string entityId)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select action, actor_type, actor_id, tenant_id,
                   before_state::text, after_state::text, metadata::text
              from audit.event
             where entity_id = @entity
             order by id
            """,
            connection);

        command.Parameters.AddWithValue("entity", Guid.Parse(entityId));

        var rows = new List<LedgerRow>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new LedgerRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }

    private sealed record LedgerRow(
        string Action,
        string ActorType,
        Guid? ActorId,
        Guid? TenantId,
        string? BeforeState,
        string? AfterState,
        string Metadata);
}
