using MautoDesk.SharedKernel;
using Microsoft.Extensions.Logging;

namespace MautoDesk.Infrastructure.Outbox;

/// <summary>
/// The default publisher: records that a message was delivered, and delivers it
/// to nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is honest rather than aspirational. No module subscribes to a domain
/// event yet, so there is nowhere for one to go. What this class buys is that
/// the <em>machinery</em> is real and exercised: messages are claimed, leased,
/// marked dispatched, retried on failure and dead-lettered, and all of that is
/// under test. When the first consumer arrives, it registers a different
/// <see cref="IEventPublisher"/> and nothing else changes.
/// </para>
/// <para>
/// It deliberately does <b>not</b> log the payload. A
/// <c>crm.customer.created</c> body is PII, and an event stream written to
/// stdout is an event stream in the log aggregator. The event type, id and
/// tenant are enough to trace a message through the system; the payload is in
/// the database for anyone entitled to read it.
/// </para>
/// </remarks>
public sealed partial class LoggingEventPublisher : IEventPublisher
{
    private readonly ILogger<LoggingEventPublisher> _logger;

    public LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) => _logger = logger;

    public Task PublishAsync(PublishedEvent message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        LogPublished(_logger, message.EventType, message.MessageId, message.TenantId, message.Attempt);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Debug,
        Message = "Published {EventType} {MessageId} for tenant {TenantId} on attempt {Attempt}. " +
                  "No consumer is registered, so this message was delivered to nothing.")]
    private static partial void LogPublished(
        ILogger logger, string eventType, Guid messageId, Guid? tenantId, int attempt);
}
