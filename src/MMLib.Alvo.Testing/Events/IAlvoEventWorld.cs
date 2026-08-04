using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// One started backend the two event acceptance criteria are measured over: the write path that queues the
/// events, the pump that drains them, the receiver that recorded what arrived, the execution-log entries the
/// run wrote, and the counters it incremented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member here exists because a criterion about <em>absence</em> is worthless without a way to prove
/// the run happened at all.</b> "Zero execution-log rows" passes trivially when nothing ran, when the
/// condition never compiled, or when no event was ever written — so the seam carries
/// <see cref="TallyAsync"/> (the events existed, and afterwards they are retired rather than still pending)
/// and <see cref="Metrics"/> (the counter's own value, not merely that it moved) beside the two absences.
/// </para>
/// <para>
/// <b><see cref="DrainAsync"/> drives the dispatcher's own pump rather than sleeping.</b> A drain that polls
/// and gives up quietly would make every count above an under-count, and would pass while the pump was
/// broken and the timeout generous.
/// </para>
/// </remarks>
public interface IAlvoEventWorld : IAsyncDisposable
{
    /// <summary>Creates one row through the data port, and answers its id.</summary>
    /// <param name="status">The row's initial <c>status</c>.</param>
    Task<Guid> CreateAsync(string status);

    /// <summary>Updates one row through the data port.</summary>
    /// <param name="id">The row to update.</param>
    /// <param name="status">The new <c>status</c>, or <see langword="null"/> to leave it alone.</param>
    /// <param name="plate">The new <c>plate</c>, or <see langword="null"/> to leave it alone.</param>
    Task UpdateAsync(Guid id, string? status = null, string? plate = null);

    /// <summary>
    /// Claims and dispatches batch after batch until the outbox has nothing claimable left.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The queue never emptied inside the implementation's own bound. It fails loudly and names how many
    /// entries were left, because a drain that gave up quietly would silently under-count every assertion
    /// made after it.
    /// </exception>
    Task DrainAsync();

    /// <summary>How many outbox entries are still undelivered, and how many have been retired.</summary>
    Task<AlvoOutboxTally> TallyAsync();

    /// <summary>Every webhook delivery the receiver recorded, in arrival order.</summary>
    IReadOnlyList<AlvoEventDelivery> Deliveries { get; }

    /// <summary>
    /// The execution log this run wrote: one entry per <em>executed action</em>, and nothing else the event
    /// subsystem logged.
    /// </summary>
    IReadOnlyList<AlvoEventLogEntry> ActionLogEntries { get; }

    /// <summary>The event counters this run incremented.</summary>
    IAlvoEventMeter Metrics { get; }
}

/// <summary>The event counters one run incremented, by instrument name.</summary>
/// <remarks>
/// A criterion asserts a counter's <em>value</em> rather than that it moved, so the probe sums measurements
/// instead of reporting the last one. Reading zero for an instrument nobody published is deliberate: it is
/// what makes a renamed instrument fail the criterion that expects a non-zero count.
/// </remarks>
public interface IAlvoEventMeter
{
    /// <summary>The sum of every measurement recorded for <paramref name="instrumentName"/>, or zero.</summary>
    /// <param name="instrumentName">The instrument's name, such as <c>alvo.events.filtered</c>.</param>
    long CountOf(string instrumentName);
}

/// <summary>One webhook delivery a receiver recorded.</summary>
/// <param name="Url">The absolute URL the delivery was posted to.</param>
/// <param name="Body">The request body, exactly as it arrived.</param>
public sealed record AlvoEventDelivery(string Url, string Body);

/// <summary>One line the event subsystem logged.</summary>
/// <param name="Name">
/// The log event's name, which for a source-generated <c>LoggerMessage</c> is the method that wrote it.
/// </param>
/// <param name="Message">The formatted message, exactly as the logging pipeline rendered it.</param>
public sealed record AlvoEventLogEntry(string Name, string Message);

/// <summary>The outbox's own state: what is still undelivered, and what has been retired.</summary>
/// <param name="Pending">Entries whose <c>dispatched_at</c> is unset.</param>
/// <param name="Retired">Entries whose <c>dispatched_at</c> is set.</param>
/// <remarks>
/// Asserted before a drain to prove the events existed, and after it to prove the pump processed them rather
/// than the criterion measuring a queue nothing ever wrote to.
/// </remarks>
public sealed record AlvoOutboxTally(int Pending, int Retired);

/// <summary>
/// The project the two criteria are measured against: the entity, its schema, the descriptor that declares
/// the after-hooks, the caller every write runs as, and the meter the counters are published on.
/// </summary>
/// <remarks>
/// One authority for all of it, so the suite's assertions and the per-engine world that stands the database
/// up cannot describe two different projects — which is exactly how a criterion comes to measure a hook that
/// was never declared on the entity being written.
/// </remarks>
/// <param name="Schema">The applied schema the database is migrated to.</param>
/// <param name="Descriptor">The descriptor the policy catalog and the after-hooks are compiled from.</param>
/// <param name="Caller">The caller every write in the suite is performed as.</param>
/// <param name="Entity">The entity every write in the suite targets.</param>
/// <param name="MeterName">The meter the event counters are published on.</param>
public sealed record AlvoEventProject(
    SchemaModel Schema,
    AlvoDescriptor Descriptor,
    AlvoContext Caller,
    string Entity,
    string MeterName);
