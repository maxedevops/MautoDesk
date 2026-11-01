using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MautoDesk.Infrastructure.Outbox;
using MautoDesk.Infrastructure.Tenancy;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The outbox dispatcher against a real PostgreSQL, as the real role.
/// </summary>
/// <remarks>
/// Every assertion here is about something that was true in the schema and false
/// in practice until now: messages were written correctly and consumed by
/// nothing. The most important test in the file is the first one — that
/// <c>mautodesk_app</c> cannot do this job — because it is the reason the
/// dispatcher has a separate database identity at all.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class OutboxDispatcherTests
{
    private readonly ApiFixture _fixture;

    public OutboxDispatcherTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The reason V0011 exists, stated as a test.
    /// </summary>
    /// <remarks>
    /// If someone later "simplifies" the configuration by pointing the
    /// dispatcher at the application connection, this fails. Without it the
    /// symptom would be a dispatcher that runs forever and delivers nothing,
    /// which looks exactly like a healthy idle system.
    /// </remarks>
    [Fact]
    public async Task The_application_role_cannot_see_the_outbox_across_tenants()
    {
        await CreateVehicleAsync();

        await using var appConnection = new NpgsqlConnection(ApiFixture.AppConnectionString);
        await appConnection.OpenAsync();

        // No tenant scope set, exactly as a background loop would find it.
        await using var command = new NpgsqlCommand(
            "select count(*)::int from app.outbox_message where dispatched_at is null",
            appConnection);

        var visible = (int)(await command.ExecuteScalarAsync())!;

        visible.Should().Be(
            0,
            "row-level security denies mautodesk_app every row when no tenant is in scope — which " +
            "is correct for a request and useless for a dispatcher");
    }

    [Fact]
    public async Task The_dispatcher_role_sees_pending_messages_and_marks_them_dispatched()
    {
        var vehicleId = await CreateVehicleAsync();

        (await PendingCountAsync(vehicleId)).Should().BeGreaterThan(0);

        var publisher = new RecordingPublisher();
        var dispatched = await NewDispatcher(publisher).RunOnceAsync(CancellationToken.None);

        dispatched.Should().BeGreaterThan(0, "there was at least one pending message");
        publisher.Published.Should().Contain(m => m.EventType == "inventory.vehicle.created");

        var stillPending = await PendingCountAsync(vehicleId);
        stillPending.Should().Be(0, "a published message is marked dispatched, not left to be sent twice");
    }

    [Fact]
    public async Task A_dispatched_message_is_not_claimed_again()
    {
        await CreateVehicleAsync();

        var first = new RecordingPublisher();
        await NewDispatcher(first).RunOnceAsync(CancellationToken.None);
        first.Published.Should().NotBeEmpty();

        var second = new RecordingPublisher();
        var count = await NewDispatcher(second).RunOnceAsync(CancellationToken.None);

        count.Should().Be(0, "the partial index and the dispatched_at guard exclude delivered messages");
        second.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_publisher_reschedules_rather_than_losing_the_message()
    {
        var vehicleId = await CreateVehicleAsync();

        var dispatcher = NewDispatcher(new ThrowingPublisher(), configure: o => o.MaxAttempts = 5);
        await dispatcher.RunOnceAsync(CancellationToken.None);

        var (attempts, deadLettered, lastError) = await ReadStateAsync(vehicleId);

        attempts.Should().Be(1);
        deadLettered.Should().BeFalse("one failure is not a reason to give up");
        lastError.Should().Contain("no consumer");

        // The lease and the backoff both push availability into the future, so
        // the next pass finds nothing — which is what stops a failing message
        // from spinning the dispatcher at full speed.
        var immediate = await NewDispatcher(new RecordingPublisher()).RunOnceAsync(CancellationToken.None);
        immediate.Should().Be(0, "a failed message backs off before it is retried");
    }

    [Fact]
    public async Task A_message_that_never_succeeds_is_dead_lettered_and_kept()
    {
        var vehicleId = await CreateVehicleAsync();

        // MaxAttempts = 1 so the first failure exhausts it. Backoff is zeroed so
        // this does not become a test that waits.
        var dispatcher = NewDispatcher(
            new ThrowingPublisher(),
            configure: o =>
            {
                o.MaxAttempts = 1;
                o.LeaseDuration = TimeSpan.Zero;
                o.BaseBackoff = TimeSpan.Zero;
            });

        await dispatcher.RunOnceAsync(CancellationToken.None);

        var (_, deadLettered, lastError) = await ReadStateAsync(vehicleId);

        deadLettered.Should().BeTrue("a message that has exhausted its attempts stops being retried");
        lastError.Should().NotBeNullOrEmpty("the reason it died is the whole value of the row");

        // Kept, not deleted. An undeliverable message is exactly the thing an
        // operator needs to look at.
        var rows = await CountRowsAsync(vehicleId);
        rows.Should().BeGreaterThan(0);

        var reclaimed = await NewDispatcher(new RecordingPublisher()).RunOnceAsync(CancellationToken.None);
        reclaimed.Should().Be(0, "dead-lettered messages are excluded from the claim query");
    }

    [Fact]
    public void Backoff_grows_exponentially_and_stops_at_the_cap()
    {
        var options = new OutboxOptions
        {
            BaseBackoff = TimeSpan.FromSeconds(10),
            MaxBackoff = TimeSpan.FromMinutes(30),
        };

        options.BackoffFor(1).Should().Be(TimeSpan.FromSeconds(10));
        options.BackoffFor(2).Should().Be(TimeSpan.FromSeconds(20));
        options.BackoffFor(3).Should().Be(TimeSpan.FromSeconds(40));

        // The cap is what keeps a long-broken consumer from scheduling its next
        // retry after the heat death of the universe.
        options.BackoffFor(40).Should().Be(TimeSpan.FromMinutes(30));
        options.BackoffFor(0).Should().Be(TimeSpan.FromSeconds(10), "attempts are 1-based");
    }

    private static OutboxDispatcher NewDispatcher(
        IEventPublisher publisher,
        Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions
        {
            ConnectionString = ApiFixture.DispatcherConnectionString,
            BatchSize = 100,
            LeaseDuration = TimeSpan.FromMinutes(5),
        };

        configure?.Invoke(options);

        return new OutboxDispatcher(
            publisher,
            Options.Create(options),
            new SystemClock(),
            NullLogger<OutboxDispatcher>.Instance);
    }

    private async Task<Guid> CreateVehicleAsync()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            // No VIN: (tenant_id, vin) is unique, and these tests care about
            // the event the save raises, not about decoding.
            new CreateVehicleCommand
            {
                StockNumber = "IT-OBX-" + Guid.NewGuid().ToString("N")[..8],
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<VehicleDto>();
        return created!.Id;
    }

    private static async Task<int> PendingCountAsync(Guid vehicleId) =>
        await ScalarAsync<int>("""
            select count(*)::int from app.outbox_message
             where payload->>'vehicleId' = @id
               and dispatched_at is null and dead_lettered_at is null
            """, vehicleId);

    private static async Task<int> CountRowsAsync(Guid vehicleId) =>
        await ScalarAsync<int>(
            "select count(*)::int from app.outbox_message where payload->>'vehicleId' = @id",
            vehicleId);

    private static async Task<(int Attempts, bool DeadLettered, string? LastError)> ReadStateAsync(
        Guid vehicleId)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select attempts, dead_lettered_at is not null, last_error
              from app.outbox_message
             where payload->>'vehicleId' = @id
             order by id desc limit 1
            """, connection);
        command.Parameters.AddWithValue("id", vehicleId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (
            reader.GetInt32(0),
            reader.GetBoolean(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is a literal authored in this file; the only value is " +
                        "bound as a parameter.")]
    private static async Task<T> ScalarAsync<T>(string sql, Guid vehicleId)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", vehicleId.ToString());

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<PublishedEvent> Published { get; } = [];

        public Task PublishAsync(PublishedEvent message, CancellationToken cancellationToken)
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>A consumer that is down, which is the case the dispatcher exists for.</summary>
    private sealed class ThrowingPublisher : IEventPublisher
    {
        public Task PublishAsync(PublishedEvent message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no consumer is reachable");
    }
}
