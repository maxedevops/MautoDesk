namespace MautoDesk.SharedKernel;

/// <summary>
/// A domain event as it leaves the outbox.
/// </summary>
/// <remarks>
/// The payload stays a JSON string rather than a deserialized object. The
/// dispatcher moves messages; it is not entitled to know what a
/// <c>inventory.vehicle.created</c> looks like, and giving it that knowledge
/// would put every module's event shape on its compile path.
/// </remarks>
/// <param name="MessageId">
/// Stable across every delivery attempt. This is the key a handler deduplicates
/// on, recorded in <c>app.processed_message</c>.
/// </param>
/// <param name="TenantId">
/// The dealership the originating aggregate belonged to. Null for a
/// platform-scope event. A handler that touches the database must establish this
/// scope before it does, or row-level security will deny it everything.
/// </param>
/// <param name="EventType">The dotted event name, e.g. <c>inventory.vehicle.created</c>.</param>
/// <param name="Payload">The serialized domain event, as JSON.</param>
/// <param name="OccurredAt">When the state change happened, not when it was published.</param>
/// <param name="CorrelationId">The trace that caused it, where one was in scope.</param>
/// <param name="Attempt">
/// Which delivery this is, starting at 1. A handler that behaves differently on
/// a retry — suppressing a duplicate notification, say — needs to know.
/// </param>
public sealed record PublishedEvent(
    Guid MessageId,
    Guid? TenantId,
    string EventType,
    string Payload,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId,
    int Attempt);

/// <summary>
/// Where an outbox message goes once it has been claimed.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0006's migration path. Today the implementation dispatches in-process;
/// the day a broker earns its place, it becomes a publish to that broker and no
/// feature code changes. That is the entire reason this interface exists rather
/// than the dispatcher calling handlers directly.
/// </para>
/// <para>
/// <b>Delivery is at-least-once.</b> A publisher that succeeds and then loses the
/// acknowledgement will be asked to publish the same <c>MessageId</c> again.
/// Handlers are idempotent by contract, keyed on the message id.
/// </para>
/// <para>
/// Throwing is how a publisher reports failure. The dispatcher catches it,
/// records the message, and reschedules with backoff — so an implementation
/// should let the real exception out rather than swallowing it and returning
/// quietly, which would mark an undelivered message as delivered.
/// </para>
/// </remarks>
public interface IEventPublisher
{
    public Task PublishAsync(PublishedEvent message, CancellationToken cancellationToken);
}
