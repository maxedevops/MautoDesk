using MautoDesk.SharedKernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace MautoDesk.Infrastructure.Outbox;

/// <summary>
/// Drains <c>app.outbox_message</c> and hands each message to <see cref="IEventPublisher"/>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0006 names Hangfire for background <em>jobs</em> — email, OCR, feeds,
/// reports — and it remains the plan for those. The outbox dispatcher is not one
/// of those: it needs a poll loop and nothing else, no scheduler, no dashboard,
/// no job storage. A hosted service does that with zero new dependencies, and
/// adding a scheduling framework to run a <c>while</c> loop would be
/// infrastructure ahead of need.
/// </para>
/// <para>
/// <b>Safe to run on every instance.</b> Claiming uses <c>FOR UPDATE SKIP
/// LOCKED</c> and moves <c>available_at</c> forward by a lease, so two
/// dispatchers never work the same row, and a dispatcher that dies mid-publish
/// releases its work when the lease expires rather than stranding it.
/// </para>
/// <para>
/// <b>The transaction is short and does not span the publish.</b> Claiming
/// commits, then publishing happens, then the outcome is recorded. Holding a
/// transaction open across an I/O call would put a slow consumer in the way of
/// every other writer on the table.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher : BackgroundService
{
    private readonly IEventPublisher _publisher;
    private readonly OutboxOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<OutboxDispatcher> _logger;

    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    public OutboxDispatcher(
        IEventPublisher publisher,
        IOptions<OutboxOptions> options,
        IClock clock,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _publisher = publisher;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            // Loud, and then idle. The alternative — falling back to the
            // application connection — would dispatch nothing while looking
            // healthy, which is the failure mode this whole class exists to end.
            LogNoConnectionString(_logger);
            return;
        }

        LogStarted(_logger, _options.PollInterval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;

            try
            {
                dispatched = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await PruneIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NpgsqlException ex)
            {
                // The database is unreachable or refusing us. Log and keep
                // looping: a dispatcher that exits on a transient outage needs a
                // process restart to come back, and nothing restarts it.
                LogPassFailed(_logger, ex);
            }

            // A full batch means there is more waiting. Going straight back for
            // it is what lets a backlog drain instead of trickling out at one
            // batch per poll interval.
            if (dispatched >= _options.BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogStopped(_logger);
    }

    /// <summary>One pass: claim a batch, publish each, record each outcome.</summary>
    /// <remarks>Internal so the integration tests can drive a single pass deterministically.</remarks>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var claimed = await ClaimAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var message in claimed)
        {
            try
            {
                await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
                await MarkDispatchedAsync(connection, message.MessageId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down mid-batch. The lease expires and the message is
                // redelivered; not recording anything is the correct thing to do.
                throw;
            }
#pragma warning disable CA1031 // A publisher may throw anything; the dispatcher must survive all of it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await RecordFailureAsync(connection, message, ex, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return claimed.Count;
    }

    /// <summary>
    /// Claims up to <see cref="OutboxOptions.BatchSize"/> messages.
    /// </summary>
    /// <remarks>
    /// One statement. The CTE selects with <c>FOR UPDATE SKIP LOCKED</c> and the
    /// outer <c>UPDATE</c> stamps the lease, so the claim is atomic against every
    /// other dispatcher without an application-level lock.
    /// </remarks>
    private async Task<IReadOnlyList<PublishedEvent>> ClaimAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            with claimed as (
              select id
                from app.outbox_message
               where dispatched_at is null
                 and dead_lettered_at is null
                 and available_at <= now()
               order by id
                 for update skip locked
               limit @batch_size
            )
            update app.outbox_message m
               set attempts     = m.attempts + 1,
                   available_at = now() + @lease
              from claimed c
             where m.id = c.id
            returning m.message_id, m.tenant_id, m.event_type, m.payload::text,
                      m.correlation_id, m.occurred_at, m.attempts
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer)
        {
            Value = _options.BatchSize,
        });
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Interval)
        {
            Value = _options.LeaseDuration,
        });

        var messages = new List<PublishedEvent>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new PublishedEvent(
                MessageId: reader.GetGuid(0),
                TenantId: await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetGuid(1),
                EventType: reader.GetString(2),
                Payload: reader.GetString(3),
                OccurredAt: reader.GetFieldValue<DateTimeOffset>(5),
                CorrelationId: await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetGuid(4),
                Attempt: reader.GetInt32(6)));
        }

        return messages;
    }

    private static async Task MarkDispatchedAsync(
        NpgsqlConnection connection,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            update app.outbox_message
               set dispatched_at = now(), last_error = null
             where message_id = @message_id and dispatched_at is null
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Uuid) { Value = messageId });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reschedules the message, or gives up on it.</summary>
    private async Task RecordFailureAsync(
        NpgsqlConnection connection,
        PublishedEvent message,
        Exception failure,
        CancellationToken cancellationToken)
    {
        // The exception message, not the stack trace. This column is read by an
        // operator in a dashboard, and a trace here would push the useful line
        // off the screen. The trace goes to the log with the same message id.
        var reason = Truncate(failure.Message, 1000);

        if (message.Attempt >= _options.MaxAttempts)
        {
            await DeadLetterAsync(connection, message.MessageId, reason, cancellationToken)
                .ConfigureAwait(false);
            LogDeadLettered(_logger, message.EventType, message.MessageId, message.Attempt, failure);
            return;
        }

        await RescheduleAsync(
            connection,
            message.MessageId,
            reason,
            _options.BackoffFor(message.Attempt),
            cancellationToken).ConfigureAwait(false);

        LogDeliveryFailed(_logger, message.EventType, message.MessageId, message.Attempt, failure);
    }

    private static async Task RescheduleAsync(
        NpgsqlConnection connection,
        Guid messageId,
        string reason,
        TimeSpan backoff,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            update app.outbox_message
               set available_at = now() + @backoff, last_error = @reason
             where message_id = @message_id
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Uuid) { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = reason });
        command.Parameters.Add(new NpgsqlParameter("backoff", NpgsqlDbType.Interval) { Value = backoff });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops retrying, and keeps both the row and the reason.</summary>
    private static async Task DeadLetterAsync(
        NpgsqlConnection connection,
        Guid messageId,
        string reason,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            update app.outbox_message
               set dead_lettered_at   = now(),
                   dead_letter_reason = @reason,
                   last_error         = @reason
             where message_id = @message_id
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Uuid) { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = reason });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes idempotency keys past their retention window.
    /// </summary>
    /// <remarks>
    /// Housekeeping rather than a separate job, because it is one statement on a
    /// schedule and the dispatcher is already the process with a cross-tenant
    /// grant on that table. Only completed keys are pruned by age; an
    /// <em>incomplete</em> one is a request that is either still running or died
    /// mid-flight, and those are cleared on a much shorter fuse so a crashed
    /// request does not block its own retry for a day.
    /// </remarks>
    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now.Add(_options.PruneInterval);

        const string Sql = """
            delete from app.idempotency_key
             where (completed_at is not null and created_at < now() - @retention)
                or (completed_at is null     and created_at < now() - @abandoned)
            """;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval)
        {
            Value = _options.IdempotencyRetention,
        });
        command.Parameters.Add(new NpgsqlParameter("abandoned", NpgsqlDbType.Interval)
        {
            Value = TimeSpan.FromMinutes(15),
        });

        var pruned = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (pruned > 0)
        {
            LogPruned(_logger, pruned);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "Outbox dispatcher started; polling every {PollInterval} in batches of {BatchSize}.")]
    private static partial void LogStarted(ILogger logger, TimeSpan pollInterval, int batchSize);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Outbox dispatcher stopped.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "Outbox dispatcher is disabled by configuration; no messages will be published.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Outbox:ConnectionString is not set, so the dispatcher will not run and no " +
                  "domain event will ever be published. Set it to a mautodesk_dispatcher " +
                  "connection; the application connection cannot see the outbox across tenants.")]
    private static partial void LogNoConnectionString(ILogger logger);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
        Message = "Outbox dispatch pass failed; retrying after the poll interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning,
        Message = "Delivery of {EventType} {MessageId} failed on attempt {Attempt}; rescheduling.")]
    private static partial void LogDeliveryFailed(
        ILogger logger, string eventType, Guid messageId, int attempt, Exception exception);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
        Message = "Dead-lettered {EventType} {MessageId} after {Attempt} attempts. The row is " +
                  "retained in app.outbox_message and will not be retried.")]
    private static partial void LogDeadLettered(
        ILogger logger, string eventType, Guid messageId, int attempt, Exception exception);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Debug,
        Message = "Pruned {Count} expired idempotency key(s).")]
    private static partial void LogPruned(ILogger logger, int count);
}
