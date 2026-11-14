namespace MautoDesk.SharedKernel;

/// <summary>What a stored idempotent response looked like.</summary>
public sealed record IdempotentResponse(int Status, string? Body);

/// <summary>The outcome of trying to claim an idempotency key.</summary>
public enum IdempotencyOutcome
{
    /// <summary>The key is new. Execute the request.</summary>
    Reserved,

    /// <summary>
    /// The key has been seen with the same request, and the original response is
    /// available. Replay it.
    /// </summary>
    Replay,

    /// <summary>
    /// The key has been seen with the same request, but the first attempt has
    /// not finished. Two identical requests are in flight.
    /// </summary>
    InProgress,

    /// <summary>
    /// The key has been seen with a <em>different</em> request. This is a client
    /// bug — a reused key — and answering it with either result would be wrong.
    /// </summary>
    Mismatch,
}

/// <summary>The result of <see cref="IIdempotencyStore.TryReserveAsync"/>.</summary>
public sealed record IdempotencyReservation(
    IdempotencyOutcome Outcome,
    IdempotentResponse? Response);

/// <summary>
/// The API-level idempotency ledger.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <c>app.idempotency_key</c>, whose primary key
/// <c>(tenant_id, key, endpoint)</c> is what makes the reservation atomic: two
/// concurrent requests with the same key race on an insert, exactly one wins,
/// and the loser is told the work is already in flight. There is no read-then-
/// write window to lose.
/// </para>
/// <para>
/// The endpoint is part of the key on purpose. A client that generates one key
/// per user action and sends it to two different endpoints is doing something
/// reasonable; treating that as a collision would break it.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Claims the key, or reports what the previous claim did.</summary>
    public Task<IdempotencyReservation> TryReserveAsync(
        string key,
        string endpoint,
        byte[] requestHash,
        CancellationToken cancellationToken);

    /// <summary>Records the response a reserved key produced.</summary>
    public Task CompleteAsync(
        string key,
        string endpoint,
        int status,
        string? body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases a reservation so the request can genuinely be retried.
    /// </summary>
    /// <remarks>
    /// Called when the request failed in a way that says nothing about whether
    /// retrying would also fail — a 5xx, or an unhandled exception. Storing that
    /// response would pin a transient outage to the key and turn a retryable
    /// failure into a permanent one.
    /// </remarks>
    public Task ReleaseAsync(string key, string endpoint, CancellationToken cancellationToken);
}
