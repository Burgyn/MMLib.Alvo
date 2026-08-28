using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <see cref="IAlvoEvents"/> over the outbox: guard the name, build the envelope, append one entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard runs before anything else, including the clock.</b> A refused name must leave no trace — no
/// id minted, no row, no counter — because the whole point is that the host never gets an entry it could
/// mistake for a real data change. It is also why the refusal is an <see cref="ArgumentException"/> rather
/// than a returned result: a refusal a caller can forget to check is a guard that is not one.
/// </para>
/// <para>
/// <b>The envelope is built through the same authorities a data event's is</b> —
/// <see cref="AlvoEventId.Create(DateTimeOffset)"/> for a monotonic id, <see cref="AlvoEvent.DefaultSource"/> for the source,
/// and <see cref="AlvoEventProvenance"/> for the actor and the correlation id — so a custom event is a
/// first-class envelope on the wire and differs only in its <c>type</c> and in what it is about. The one
/// thing it cannot share is the <c>partitionkey</c> shape: there is no entity and no row id, so the
/// host-supplied subject is the key, and per-subject ordering is the only ordering it can be given.
/// </para>
/// </remarks>
/// <param name="outbox">The queue the entry is appended to.</param>
/// <param name="clock">Where the event's own instant comes from; never <see cref="DateTimeOffset.UtcNow"/>.</param>
internal sealed class AlvoEvents(IOutboxStore outbox, TimeProvider clock) : IAlvoEvents
{
    /// <inheritdoc/>
    public Task PublishAsync(
        string type,
        string subject,
        IReadOnlyDictionary<string, object?>? data,
        AlvoContext context,
        CancellationToken cancellationToken = default)
    {
        AlvoEventName.EnsureCustom(type, nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(context);

        return outbox.AppendAsync(Envelope(type, subject, data, context), cancellationToken);
    }

    /// <summary>The envelope one published event becomes.</summary>
    /// <param name="type">The guarded event name.</param>
    /// <param name="subject">What the event is about, and therefore its partition key.</param>
    /// <param name="data">The payload, or <see langword="null"/>.</param>
    /// <param name="context">The caller, as provenance.</param>
    private AlvoEvent Envelope(
        string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context)
    {
        var now = clock.GetUtcNow();
        var id = AlvoEventId.Create(now);

        return new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = subject,
            AuthType = AlvoEventProvenance.AuthTypeOf(context),
            AuthId = AlvoEventProvenance.AuthIdOf(context),
            CorrelationId = AlvoEventProvenance.CorrelationIdOf(id),
            Data = new AlvoEventData { Record = data is null ? null : new AlvoRecord(data) },
        };
    }
}
