namespace MautoDesk.Infrastructure.Outbox;

/// <summary>Configuration for the outbox dispatcher.</summary>
/// <remarks>
/// Bound from the <c>Outbox</c> configuration section. Every default here is the
/// value intended for production, so a misconfigured environment runs
/// conservatively rather than aggressively.
/// </remarks>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// The dispatcher's own connection string, as <c>mautodesk_dispatcher</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not the application connection.</b> <c>mautodesk_app</c> is subject to
    /// the tenant-isolation policy on <c>app.outbox_message</c> and, having no
    /// tenant in scope, would see zero rows and dispatch nothing — silently,
    /// forever. V0011 creates a role that can see across tenants and can reach
    /// nothing else.
    /// <para>
    /// Left unset, the dispatcher does not start and says so at warning level.
    /// Refusing to run beats falling back to the application connection and
    /// appearing to work in development while doing nothing in production.
    /// </para>
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>How long to wait after finding nothing to do.</summary>
    /// <remarks>
    /// Five seconds is the ADR-0006 tradeoff: outbox lag stays well inside the
    /// <c>TD-001</c> threshold (p99 &gt; 5 s sustained is when a broker earns its
    /// place) without polling an idle database twice a second. A batch that comes
    /// back full skips the wait entirely, so a backlog drains at full speed.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages claimed per pass.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long a claimed message stays invisible to other dispatchers.
    /// </summary>
    /// <remarks>
    /// The lease is what makes running more than one instance safe: a claim moves
    /// <c>available_at</c> forward, so a sibling's <c>SKIP LOCKED</c> query will
    /// not pick the same row. If this process dies mid-publish, the lease expires
    /// and the message is retried — at-least-once, which is the contract.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Deliveries attempted before the message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>First retry delay; doubles each attempt, capped by <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a completed idempotency key is honoured.
    /// </summary>
    /// <remarks>24 hours, per docs/04-api-contracts.md §6.</remarks>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often expired idempotency keys are pruned.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Set false to keep the dispatcher out of a process that should not run it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The retry delay for a given attempt, exponential and capped.</summary>
    /// <remarks>
    /// Deliberately deterministic — no jitter. Jitter spreads a thundering herd
    /// across many clients; here there is one dispatcher per instance draining a
    /// leased queue, so the herd does not exist and a reproducible schedule is
    /// worth more than a randomised one when reading an incident timeline.
    /// </remarks>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 1)
        {
            return BaseBackoff;
        }

        // Shift rather than Math.Pow: no doubles anywhere near this codebase, and
        // the exponent is bounded so the shift cannot overflow.
        var shift = Math.Min(attempt - 1, 20);
        var ticks = BaseBackoff.Ticks * (1L << shift);

        return ticks >= MaxBackoff.Ticks || ticks < 0
            ? MaxBackoff
            : TimeSpan.FromTicks(ticks);
    }
}
