namespace MMLib.Alvo.Events;

/// <summary>
/// How the outbox dispatcher drains the event queue: whether it runs at all, how often it polls, how much it
/// claims at a time, how many attempts an event gets, and how long a claim holds. Bound from the
/// <see cref="SectionName"/> configuration section by <c>AddAlvo</c> and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// In an environment variable the keys are spelled with a double underscore for the separator —
/// <c>Alvo__Events__Enabled</c>, <c>Alvo__Events__BatchSize</c>, and so on.
/// </para>
/// <para>
/// <b>Every default here is a latency-against-load trade, with one exception.</b>
/// <see cref="MaxAttempts"/> is the only bound on an event that can never be delivered, because this build has
/// no dead-letter queue: past the ceiling the entry stops being claimed and stays in the outbox with its
/// <c>dispatched_at</c> unset, so it is countable and inspectable rather than deleted or moved. Raising it
/// raises how long one poison event is retried; lowering it gives a genuinely transient outage less room.
/// </para>
/// <para>
/// <b>Exactly one dispatcher per project is supported.</b> Per-entity-key ordering holds with one dispatcher
/// <em>and</em> no two events for one key inside the same millisecond; two replicas both draining one outbox
/// break the first half silently, because nothing here takes a distributed lock (issue #150). Running one
/// replica with the dispatcher on and the rest with <see cref="Enabled"/> off is the supported shape.
/// </para>
/// </remarks>
public sealed class AlvoEventOptions
{
    /// <summary>The configuration section these options bind from: <c>Alvo:Events</c>.</summary>
    public const string SectionName = "Alvo:Events";

    /// <summary>
    /// Gets or sets whether this process drains the outbox. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Switching it off stops delivery, never emission: every write still appends its event on its own
    /// transaction, so the queue keeps filling and a later process — or this one, restarted — delivers what
    /// accumulated. That is what makes it the switch for the replicas that must not dispatch, rather than a
    /// way to turn events off.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how long the pump waits after finding nothing to claim, before claiming again. Defaults to
    /// one second, and must be greater than zero.
    /// </summary>
    /// <remarks>
    /// Waited only after an <em>empty</em> claim, so a queue with a backlog drains at full speed and this is
    /// the idle-latency setting rather than a throughput limit.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the most entries one claim takes. Defaults to 100, and must be at least 1.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets how many times one event may be claimed before it is left alone. Defaults to 10, and must
    /// be at least 1.
    /// </summary>
    /// <remarks>
    /// This build's stand-in for a dead-letter queue, and the only bound on a delivery that fails forever. A
    /// failed attempt is never classified — a 500, a 404, a DNS failure and a timeout are indistinguishable at
    /// delivery from an endpoint whose deploy is thirty seconds out — so the ceiling is the one place the retry
    /// stops.
    /// </remarks>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// Gets or sets how long a claim holds before another claimant may take the entry back. Defaults to five
    /// minutes, and must be longer than <see cref="PollInterval"/>.
    /// </summary>
    /// <remarks>
    /// The lease is what recovers an entry a process died holding; nothing else does. It must outlast the poll
    /// interval, because a lease shorter than the interval re-claims an entry that is still in flight on the
    /// very next tick — a duplicate delivery per tick rather than at-least-once delivery.
    /// </remarks>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);
}
