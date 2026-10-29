using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <see cref="IAlvoEvents"/> over the outbox: guard the name, build the envelope, append one entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name, subject, caller and payload guards run first, in that order — unchanged — then the refusal inside a
/// transaction unit, then the depth refusal, then the clock.</b> A refused name or depth must leave no trace — no
/// id minted, no row, no counter — because the whole point is that the host never gets an entry it could
/// mistake for a real data change. It is also why the refusal is an <see cref="ArgumentException"/> rather
/// than a returned result: a refusal a caller can forget to check is a guard that is not one.
/// </para>
/// <para>
/// The envelope is built by <see cref="AlvoCustomEnvelope"/>, the one builder the transactional publish shares —
/// <see cref="AlvoEventId.Create(DateTimeOffset)"/> for a monotonic id, <see cref="AlvoEvent.DefaultSource"/> for the source,
/// and <see cref="AlvoEventProvenance"/> for the actor and the correlation id — so a custom event is a
/// first-class envelope on the wire and differs only in its <c>type</c> and in what it is about. The one
/// thing it cannot share is the <c>partitionkey</c> shape: there is no entity and no row id, so the
/// host-supplied subject is the key, and per-subject ordering is the only ordering it can be given.
/// </para>
/// </remarks>
/// <param name="outbox">The queue the entry is appended to.</param>
/// <param name="clock">Where the event's own instant comes from; never <see cref="DateTimeOffset.UtcNow"/>.</param>
/// <param name="depthLimit">The causation-chain limit a publish is refused beyond, the same one every write face enforces.</param>
internal sealed class AlvoEvents(IOutboxStore outbox, TimeProvider clock, AlvoChainDepthLimit depthLimit) : IAlvoEvents
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
        AlvoCustomEnvelope.EnsureWritable(data);
        AlvoFlow.RefuseInsideUnit("IAlvoEvents.PublishAsync");
        depthLimit.EnsureWithin(context);

        return outbox.AppendAsync(AlvoCustomEnvelope.Build(type, subject, data, context, clock.GetUtcNow()), cancellationToken);
    }
}
