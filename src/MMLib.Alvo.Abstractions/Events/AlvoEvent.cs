using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// One thing that happened, as a CloudEvents 1.0.2 envelope: a row created, updated or deleted, carried
/// from the transaction that committed it to whatever runs after it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written, and that is a decision.</b> <c>MMLib.Alvo.Abstractions</c> may take no external
/// dependency (<c>docs/architecture/package-boundary.md</c>), so the envelope cannot be the CloudEvents
/// SDK's type. Nothing in Alvo needs the SDK at run time either — Alvo serializes this envelope itself for
/// the outbox row and for webhook delivery — so the SDK is a <b>test-only</b> conformance oracle instead of
/// a shipped dependency.
/// </para>
/// <para>
/// <b>Why the row images are not attributes.</b> CloudEvents context attribute values are limited to seven
/// types and none of them is a map or an array, so <c>record</c>, <c>old_record</c> and the changed-field
/// list cannot be attributes at any spelling. They live in <see cref="Data"/>, where the JSON is
/// unrestricted.
/// </para>
/// <para>
/// <b>The 64 KB rule this envelope can exceed.</b> CloudEvents requires intermediaries to forward events of
/// 64 KB or less, and <see cref="AlvoEventData.Record"/> plus
/// <see cref="AlvoEventData.OldRecord"/> on a wide row can pass that by themselves. The registered escape
/// is the <c>dataref</c> (claim-check) extension, which this build documents and does not implement,
/// because Alvo's own outbox is not an intermediary and no wire hop in F3 is bound by the rule. Tracked in
/// issue #151.
/// </para>
/// <para>
/// <b>Ordering.</b> <see cref="Id"/> is the queue order — see <see cref="AlvoEventId"/>, and note that
/// per-entity-key ordering holds only while one dispatcher runs <em>and</em> no two events for one key are
/// written inside the same millisecond.
/// </para>
/// </remarks>
public sealed record AlvoEvent
{
    /// <summary>The CloudEvents specification version written on the wire. Not <c>1.0.2</c>: the spec's own
    /// <c>specversion</c> value for the 1.0 line is <c>1.0</c>.</summary>
    public const string SpecVersion = "1.0";

    /// <summary>The media type of <see cref="Data"/>.</summary>
    public const string DataContentType = "application/json";

    /// <summary>The <c>source</c> Alvo's own data events carry.</summary>
    public const string DefaultSource = "/alvo";

    /// <summary>The <see cref="PayloadVersion"/> this build produces.</summary>
    public const int CurrentPayloadVersion = 1;

    private readonly DateTimeOffset _time;

    /// <summary>Gets the event's unique identifier, and the order events are dispatched in.</summary>
    /// <remarks>
    /// A UUIDv7 minted by <see cref="AlvoEventId.Create()"/>, never by <c>Guid.CreateVersion7()</c>
    /// directly: the outbox claims in <c>ORDER BY id</c>, and the plain BCL mint sorts about half of its
    /// same-millisecond pairs backwards.
    /// </remarks>
    public required Guid Id { get; init; }

    /// <summary>Gets the context the event occurred in (CloudEvents <c>source</c>).</summary>
    public required string Source { get; init; }

    /// <summary>Gets the event type, <c>entity.{entity}.{created|updated|deleted}</c> for a data change.</summary>
    /// <remarks>
    /// The grammar is the descriptor's own <c>eventPattern</c>, so every type this envelope can carry is a
    /// type a rule could subscribe to.
    /// </remarks>
    public required string Type { get; init; }

    /// <summary>Gets the instant the change committed, always UTC (CloudEvents <c>time</c>).</summary>
    /// <remarks>
    /// An offset is a spelling of a timestamp, never part of its value
    /// (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one instant</em>). The driver
    /// normalises through its own <c>StoredInstant</c> before constructing an event; this guard is the same
    /// rule at the envelope's boundary, where that helper is not reachable.
    /// </remarks>
    public required DateTimeOffset Time
    {
        get => _time;
        init => _time = value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException(
                "An event's time must be UTC; convert with ToUniversalTime() before constructing the "
                + "event. An offset is a spelling of a timestamp, never part of its value.",
                nameof(Time));
    }

    /// <summary>Gets what the event is about, <c>{entity}/{id}</c> for a data change.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the key events for one row are ordered by, <c>{entity}:{id}</c> for a data change.</summary>
    /// <remarks>
    /// <b>Provenance.</b> <c>partitionkey</c> <em>is</em> registered — it is the Partitioning extension,
    /// one of the five known extensions v1.0.2 lists. The outbox column carries the same value under the
    /// same name (<c>partition_key</c>) so the column and the attribute cannot drift, which is what makes
    /// F7's partitioned claim an additive change rather than a migration of a shipped table.
    /// </remarks>
    public required string PartitionKey { get; init; }

    /// <summary>Gets how the caller authenticated — one of <see cref="AlvoEventAuthType"/>'s three values.</summary>
    /// <remarks>
    /// <b>Provenance.</b> <c>authtype</c>/<c>authid</c> are the community's Auth Context extension names,
    /// and they are <em>not</em> in the v1.0.2 registry — that lists exactly five known extensions
    /// (Dataref, Distributed Tracing, Partitioning, Sampling, Sequence). They live in
    /// <c>cloudevents/extensions/authcontext.md</c> on <c>main</c> (post-1.0.2). They are adopted anyway,
    /// because they are the community's names, they satisfy the naming rule, and inventing <c>actor</c>
    /// would be worse. Alvo needs the distinction they carry: §3.3's "as system / as the originator"
    /// cannot be expressed by one opaque actor string.
    /// </remarks>
    public required string AuthType { get; init; }

    /// <summary>Gets the id shared by everything in one end-to-end flow.</summary>
    /// <remarks>
    /// <b>Provenance.</b> <c>correlationid</c>/<c>causationid</c> are the community's Correlation extension
    /// names and are likewise <em>not</em> in the v1.0.2 registry; they live in
    /// <c>cloudevents/extensions/correlation.md</c> on <c>main</c>. The pair is adopted rather than one id,
    /// because the end-to-end trace §2.12 asks for needs to distinguish "the same flow" from "the immediate
    /// cause".
    /// </remarks>
    public required string CorrelationId { get; init; }

    /// <summary>Gets the version of the <see cref="Data"/> shape.</summary>
    /// <remarks>
    /// A deliberate deviation: the specification assigns this job to <c>type</c> plus <c>dataschema</c>. It
    /// is kept because an in-process subscriber switching on an integer is cheaper than parsing a URI, and
    /// it is recorded here rather than discovered by whoever notices that the two can disagree — if they
    /// ever do, <c>type</c> wins and this member is the one that is wrong.
    /// </remarks>
    public int PayloadVersion { get; init; } = CurrentPayloadVersion;

    /// <summary>Gets how many events deep the causation chain is; <c>0</c> for a direct caller change.</summary>
    /// <remarks>
    /// Always <c>0</c> in this build, because nothing yet runs a data action <em>because of</em> an event.
    /// The member exists now so that the wire shape does not change when automation starts setting it.
    /// </remarks>
    public int ChainDepth { get; init; }

    /// <summary>Gets which credential acted, when there was one; <see langword="null"/> otherwise.</summary>
    public string? AuthId { get; init; }

    /// <summary>Gets the id of this event's immediate cause, when it had one.</summary>
    /// <remarks>
    /// <see langword="null"/> in this build, for the same reason <see cref="ChainDepth"/> is <c>0</c>. It is
    /// never defaulted to <see cref="Id"/>: an event with no cause must read as having none, or a consumer
    /// walking the chain finds a self-reference.
    /// </remarks>
    public string? CausationId { get; init; }

    /// <summary>Gets the payload: the row images and the list of fields whose value moved.</summary>
    public required AlvoEventData Data { get; init; }
}

/// <summary>
/// An <see cref="AlvoEvent"/>'s payload: the row after the change, the row before it, and which fields
/// moved.
/// </summary>
/// <remarks>
/// <para>
/// All three live here rather than as context attributes because CloudEvents' seven-type system has no map
/// and no array. Inside <c>data</c> the JSON is Alvo's own, so the members are spelled <c>record</c>,
/// <c>old_record</c> and <c>changed</c> — <c>snake_case</c>, matching every other row-shaped payload the
/// framework emits.
/// </para>
/// <para>
/// <b>The record is unmasked.</b> No <c>hidden</c> mask is applied, because an after-hook condition reading
/// <c>old.commission_note</c> or <c>changed(commission_note)</c> must see every field, and <c>hidden</c> is
/// a per-caller read mask rather than a data classification. The consequence is real and accepted: a
/// webhook delivers hidden fields to the endpoint declared in the same descriptor by the same author as the
/// <c>hidden</c> rule. Per-endpoint field projection is tracked in issue #152.
/// </para>
/// </remarks>
public sealed record AlvoEventData
{
    private readonly IReadOnlyList<string> _changed = [];

    /// <summary>Gets the row as it is after the change; <see langword="null"/> on a delete.</summary>
    public AlvoRecord? Record { get; init; }

    /// <summary>Gets the row as it was before the change; <see langword="null"/> on a create.</summary>
    public AlvoRecord? OldRecord { get; init; }

    /// <summary>Gets the fields whose value moved — every field on a create, every field on a delete.</summary>
    /// <remarks>
    /// Carried rather than recomputed per subscription, because <c>changed(field)</c> is evaluated once per
    /// matching hook and the images are the expensive part of the envelope to walk.
    /// </remarks>
    public IReadOnlyList<string> Changed
    {
        get => _changed;
        init => _changed = value ?? [];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Written by hand for one member: the compiler-generated equality would compare
    /// <see cref="Changed"/> by reference, so two payloads naming the same changed fields would be unequal
    /// and every round-trip fact would rest on identity instead of value.
    /// </remarks>
    public bool Equals(AlvoEventData? other) =>
        other is not null
        && Record == other.Record
        && OldRecord == other.OldRecord
        && Changed.SequenceEqual(other.Changed, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Record);
        hash.Add(OldRecord);
        foreach (var field in Changed)
        {
            hash.Add(field, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
