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
        EnsureWritable(data);

        return outbox.AppendAsync(
            AlvoCustomEvent.Create(Envelope(type, subject, data, context)), cancellationToken);
    }

    /// <summary>Refuses a payload value the envelope cannot express, here rather than at serialization.</summary>
    /// <param name="data">The payload the host supplied.</param>
    /// <remarks>
    /// <para>
    /// <b>The declared parameter type is wider than the wire format, so the gap is closed by a refusal at the
    /// call.</b> <c>IReadOnlyDictionary&lt;string, object?&gt;</c> accepts a nested dictionary, an array or a
    /// <c>JsonElement</c> — the obvious things to relay from inbound JSON — and
    /// <see cref="AlvoEventJson"/> refuses all of them. Without this, that refusal surfaced from deep inside
    /// <see cref="IOutboxStore.AppendAsync"/> as a <see cref="NotSupportedException"/> advising the caller to
    /// use "one of the field types the schema allows", which is meaningless for a custom event that has no
    /// schema. Found by review; the path had no test because nothing round-tripped a payload.
    /// </para>
    /// <para>
    /// <b>It writes the payload rather than pattern-matching a type list</b>, so this cannot drift from what
    /// <see cref="AlvoEventJson"/> really accepts — a second copy of that list is how one comes to admit a
    /// type the other refuses. The cost is one throwaway serialization per publish, on a path that is already
    /// about to serialize the same payload for real.
    /// </para>
    /// </remarks>
    private static void EnsureWritable(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null)
        {
            return;
        }

        try
        {
            _ = AlvoEventJson.Write(Probe(data));
        }
        catch (NotSupportedException failure)
        {
            throw new ArgumentException(
                "This event's payload carries a value the envelope cannot express. An event payload is a flat "
                + "record of scalars — text, boolean, uuid, a number, or a date/timestamp — so a nested "
                + "dictionary, an array or a JsonElement has nothing to become on the wire. Flatten it, or "
                + $"serialize that value to a string yourself. {failure.Message}",
                nameof(data),
                failure);
        }
    }

    /// <summary>The cheapest envelope that carries <paramref name="data"/> through the real writer.</summary>
    /// <param name="data">The payload being checked.</param>
    private static AlvoEvent Probe(IReadOnlyDictionary<string, object?> data) => new()
    {
        Id = Guid.Empty,
        Source = AlvoEvent.DefaultSource,
        Type = ProbeType,
        Time = DateTimeOffset.UnixEpoch,
        Subject = ProbeSubject,
        PartitionKey = ProbeSubject,
        AuthType = AlvoEventAuthType.Anonymous,
        CorrelationId = string.Empty,
        Data = new AlvoEventData { Record = new AlvoRecord(data) },
    };

    private const string ProbeType = "alvo.payload.probe";
    private const string ProbeSubject = "probe";

    /// <summary>
    /// The partition a custom event orders within: its type and its subject, joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The type is in the key so a custom event can never land in a data entity's partition.</b> A data
    /// event's key is <c>{entity}:{rowId}</c>, and <c>OutboxTable</c>'s <c>partition_key</c> column exists for
    /// F7's partitioned claim (<b>#150</b>) to index — so a host publishing <c>subject: "deals:&lt;guid&gt;"</c>
    /// would, on the day that claim reads the column, be ordering itself into a real entity's partition. That
    /// is the same "meaning silently widens when the feature lands, with nobody re-reading the artifact"
    /// hazard the wildcard refusal exists for, and it is closed by shape rather than by a warning.
    /// </para>
    /// <para>
    /// <b>Enforced at the door, not only produced here.</b> <see cref="AlvoCustomEvent.Create"/> refuses an
    /// envelope whose partition key does not start with its own type, so a host appending through
    /// <see cref="IOutboxStore.AppendAsync"/> directly cannot claim a data entity's partition either. This
    /// method is where a well-formed key is <em>built</em>; that check is where the guarantee <em>holds</em>.
    /// </para>
    /// <para>
    /// <b>The disjointness is provable, not probable.</b> An entity name is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> (<c>schema/project.schema.json</c>, <c>entities</c>' <c>propertyNames</c>)
    /// and so contains no dot; <see cref="AlvoEventName"/> requires a custom type to contain <em>at least
    /// one</em>. So the two key spaces cannot collide, whatever subject a host supplies.
    /// </para>
    /// <para>
    /// Ordering is still per subject, because the type is fixed for a given subject's stream — under the same
    /// one-dispatcher, one-millisecond conditions as everything else on this queue.
    /// </para>
    /// </remarks>
    /// <param name="type">The guarded event name.</param>
    /// <param name="subject">What the event is about, in the host's own vocabulary.</param>
    private static string PartitionKeyFor(string type, string subject) => $"{type}:{subject}";

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
            PartitionKey = PartitionKeyFor(type, subject),
            AuthType = AlvoEventProvenance.AuthTypeOf(context),
            AuthId = AlvoEventProvenance.AuthIdOf(context),
            CorrelationId = AlvoEventProvenance.CorrelationIdOf(id),
            Data = new AlvoEventData { Record = data is null ? null : new AlvoRecord(data) },
        };
    }
}
