using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// The one builder of a custom application event's envelope: the name and payload guards, then the envelope through the
/// same authorities a data event's is built by.
/// </summary>
/// <remarks>
/// Internal and in Abstractions because two publishers build it: the core's autocommit <c>IAlvoEvents</c>, and the EF
/// driver's transactional <c>IAlvoTransaction.PublishAsync</c>. One builder, so the two cannot drift.
/// </remarks>
internal static class AlvoCustomEnvelope
{
    /// <summary>Refuses a reserved or malformed name, a blank subject, or a payload the envelope cannot express — before anything is minted.</summary>
    /// <param name="type">The event's name.</param>
    /// <param name="subject">What the event is about.</param>
    /// <param name="data">The payload, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The name, subject or payload is refused.</exception>
    internal static void Ensure(string type, string subject, IReadOnlyDictionary<string, object?>? data)
    {
        AlvoEventName.EnsureCustom(type, nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        EnsureWritable(data);
    }

    /// <summary>The envelope one checked event becomes, at <paramref name="now"/>.</summary>
    /// <param name="type">The guarded event name.</param>
    /// <param name="subject">What the event is about, and therefore its partition key.</param>
    /// <param name="data">The payload, or <see langword="null"/>.</param>
    /// <param name="context">The caller, as provenance.</param>
    /// <param name="now">The event's instant, from the publisher's clock.</param>
    /// <returns>The guarded custom event.</returns>
    internal static AlvoCustomEvent Build(
        string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = AlvoEventId.Create(now);
        var stamp = AlvoEnvelopeStamp.Of(context, id, context.Tenant);

        return AlvoCustomEvent.Create(stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = PartitionKeyFor(subject),
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData { Record = data is null ? null : new AlvoRecord(data) },
        }));
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
    internal static void EnsureWritable(IReadOnlyDictionary<string, object?>? data)
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
        AuthType = AlvoEventAuthType.Unauthenticated,
        CorrelationId = string.Empty,
        Data = new AlvoEventData { Record = new AlvoRecord(data) },
    };

    private const string ProbeType = "alvo.payload.probe";
    private const string ProbeSubject = "probe";

    /// <summary>
    /// The partition a custom event orders within: a fixed marker and its subject, joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fixed marker, not the event's type — because the type broke the ordering this API promises.</b>
    /// The first draft used <c>{type}:{subject}</c>, which put <c>orders.approved</c> and
    /// <c>orders.shipped</c> for one <c>orders/42</c> into <em>different</em> partitions while
    /// <see cref="IAlvoEvents"/> promised ordering per subject. Caught in review. A fixed marker satisfies
    /// both: every event about one subject shares a key, and the marker's dot keeps the key out of any data
    /// entity's partition.
    /// </para>
    /// <para>
    /// <b>Why staying out of a data entity's partition matters.</b> A data
    /// event's key is <c>{entity}:{rowId}</c>, and <c>OutboxTable</c>'s <c>partition_key</c> column exists for
    /// F7's partitioned claim (<b>#150</b>) to index — so a host publishing <c>subject: "deals:&lt;guid&gt;"</c>
    /// would, on the day that claim reads the column, be ordering itself into a real entity's partition. That
    /// is the same "meaning silently widens when the feature lands, with nobody re-reading the artifact"
    /// hazard the wildcard refusal exists for, and it is closed by shape rather than by a warning.
    /// </para>
    /// <para>
    /// <b>Enforced at the door, not only produced here.</b> <see cref="AlvoCustomEvent.Create"/> refuses an
    /// envelope whose partition key's first segment carries no dot, so a host appending through
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
    /// <param name="subject">What the event is about, in the host's own vocabulary.</param>
    private static string PartitionKeyFor(string subject) => $"{CustomPartitionMarker}:{subject}";

    /// <summary>
    /// The fixed first segment of every custom event's partition key.
    /// </summary>
    /// <remarks>
    /// <b>It contains a dot, and that is the entire mechanism.</b> An entity name is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> (<c>schema/project.schema.json</c>, <c>entities</c>'
    /// <c>propertyNames</c>) and therefore never contains one, so no custom event's key can equal a data
    /// event's <c>{entity}:{rowId}</c> — whatever subject a host supplies.
    /// </remarks>
    private const string CustomPartitionMarker = "custom.event";
}
