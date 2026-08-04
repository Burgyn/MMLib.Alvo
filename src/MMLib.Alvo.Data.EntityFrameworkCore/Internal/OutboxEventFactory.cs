using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;

using System.Diagnostics;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Builds the <see cref="AlvoEvent"/> one write emits, out of the images that write already holds. No SQL, no
/// I/O, and no clock read of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The instant arrives as a parameter</b>, because it is the write's own audit instant: the envelope's
/// <c>time</c>, the outbox row's <c>created_at</c> and the millisecond embedded in
/// <see cref="AlvoEvent.Id"/> are then one instant rather than three clock reads
/// (<c>docs/architecture/data-path.md</c>, <em>Every timestamp is one instant</em>).
/// </para>
/// <para>
/// <b>Provenance lives in the envelope and nowhere else.</b> The actor, the correlation id and the chain
/// depth are attributes of this event rather than columns of the outbox row, so there is one authority for
/// each value instead of two that can disagree; only <c>partition_key</c> is duplicated into a column, and
/// only because F7's partitioned claim must index it.
/// </para>
/// </remarks>
internal static class OutboxEventFactory
{
    /// <summary>
    /// The event one write emits.
    /// </summary>
    /// <param name="entity">The entity written, as the applied schema declares it.</param>
    /// <param name="operation">Which of the three write faces produced this event.</param>
    /// <param name="context">The caller the write was performed as — never an ambient accessor.</param>
    /// <param name="now">The write's own audit instant, already UTC.</param>
    /// <param name="postImage">The row after the change, <b>unmasked</b>; <see langword="null"/> on a delete.</param>
    /// <param name="preImage">The row before the change, <b>unmasked</b>; <see langword="null"/> on a create.</param>
    /// <remarks>
    /// Both images are unmasked on purpose (and that is a disclosure worth naming): an after-hook condition
    /// reading <c>old.commission_note</c> or <c>changed(commission_note)</c> has to see every field, and
    /// <c>hidden</c> is a per-caller <em>read</em> mask rather than a data classification. A masked
    /// post-image would be worse than incomplete — every masked field would read as moved on every update,
    /// so <see cref="AlvoEventData.Changed"/> would report changes that never happened. Per-endpoint field
    /// projection for deliveries is tracked in issue #152.
    /// </remarks>
    internal static AlvoEvent For(
        EntitySchema entity,
        OutboxOperation operation,
        AlvoContext context,
        DateTimeOffset now,
        AlvoRecord? postImage,
        AlvoRecord? preImage)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        var rowId = RowIdOf(postImage ?? preImage);
        var id = AlvoEventId.Create(now);

        return new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = $"entity.{entity.Name}.{Suffix(operation)}",
            Time = now,
            Subject = $"{entity.Name}/{rowId}",
            PartitionKey = PartitionKeyFor(entity.Name, rowId),
            AuthType = AuthTypeOf(context),
            AuthId = AuthIdOf(context),
            CorrelationId = CorrelationIdOf(id),
            Data = new AlvoEventData
            {
                Record = postImage,
                OldRecord = preImage,
                Changed = ChangedFields(postImage, preImage),
            },
        };
    }

    /// <summary>The key every event for one row shares.</summary>
    /// <param name="entity">The entity's name.</param>
    /// <param name="rowId">The row's own id.</param>
    /// <remarks>
    /// The entity is part of it, so two entities that happen to hold one row id are two partitions rather
    /// than one — which is what makes F7's partitioned claim an ordering guarantee per row instead of per
    /// coincidence.
    /// </remarks>
    internal static string PartitionKeyFor(string entity, Guid rowId) => $"{entity}:{rowId}";

    /// <summary>
    /// The fields whose value moved: every field on a create, every field on a delete, and only the ones
    /// that really differ on an update.
    /// </summary>
    /// <param name="postImage">The row after the change, or <see langword="null"/> on a delete.</param>
    /// <param name="preImage">The row before the change, or <see langword="null"/> on a create.</param>
    /// <remarks>
    /// Carried in the payload rather than recomputed per subscription, because <c>changed(field)</c> is
    /// evaluated once per matching hook and the images are the expensive part of the envelope to walk. The
    /// list is ordered ordinally so one write produces one payload, byte for byte, whatever order the
    /// engine returned the columns in.
    /// </remarks>
    internal static IReadOnlyList<string> ChangedFields(AlvoRecord? postImage, AlvoRecord? preImage) =>
        postImage is null || preImage is null
            ? [.. EveryField(postImage ?? preImage)]
            : [.. MovedFields(postImage, preImage)];

    private static IEnumerable<string> EveryField(AlvoRecord? record) =>
        record is null ? [] : record.Values.Keys.Order(StringComparer.Ordinal);

    private static IEnumerable<string> MovedFields(AlvoRecord postImage, AlvoRecord preImage) =>
        postImage.Values.Keys
            .Union(preImage.Values.Keys, StringComparer.Ordinal)
            .Where(field => !Equals(postImage[field], preImage[field]))
            .Order(StringComparer.Ordinal);

    /// <summary>The third segment of the event type, matching the descriptor's own <c>eventPattern</c>.</summary>
    private static string Suffix(OutboxOperation operation) => operation switch
    {
        OutboxOperation.Created => "created",
        OutboxOperation.Updated => "updated",
        OutboxOperation.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, OperationOutOfRangeMessage),
    };

    private const string OperationOutOfRangeMessage =
        "Every write face has to name its own event type, or a rule could subscribe to a type nothing emits.";

    /// <summary>
    /// How the caller authenticated. Authentication, never authorization — a role is not an answer here,
    /// because an after-hook has to tell "the framework did this" from "the originator did this" and a role
    /// says neither.
    /// </summary>
    private static string AuthTypeOf(AlvoContext context) =>
        context.User == _anonymousUser ? AlvoEventAuthType.Anonymous
        : context.User == _systemUser ? AlvoEventAuthType.System
        : AlvoEventAuthType.ApiKey;

    /// <summary>
    /// Which credential acted, or <see langword="null"/> when none did. The anonymous caller's reserved
    /// all-zero id means "no identity", so reporting it would assert that an identified caller wrote the row.
    /// </summary>
    private static string? AuthIdOf(AlvoContext context) =>
        context.User == _anonymousUser ? null : context.User.Value.ToString();

    /// <summary>
    /// The id everything in one end-to-end flow shares: the ambient W3C trace id when there is one, and
    /// otherwise this event's own id.
    /// </summary>
    /// <remarks>
    /// <see cref="Activity"/> is in the BCL, so this needs no dependency, and the trace id is exactly what
    /// the specification's end-to-end trace asks for. It falls back to the event's own id rather than to
    /// <see langword="null"/> because the attribute is required — an event with no ambient trace still
    /// belongs to a flow, namely its own. <see cref="AlvoEvent.CausationId"/> stays
    /// <see langword="null"/> in this build: nothing yet runs a data action <em>because of</em> an event.
    /// </remarks>
    private static string CorrelationIdOf(Guid id) => Activity.Current?.TraceId.ToString() ?? id.ToString();

    /// <summary>
    /// The row this event is about. Taken from whichever image the operation has, never from a second read.
    /// </summary>
    private static Guid RowIdOf(AlvoRecord? image) =>
        image?[AlvoManagedColumns.Id] is Guid rowId
            ? rowId
            : throw new InvalidOperationException(
                "An event describes one row and neither image carried that row's id, so no subject and no "
                + "partition key could be formed. The image a write emits from is always the row it just "
                + "read back, so this is an invariant of this data path rather than a caller's mistake.");

    /// <inheritdoc cref="AuthTypeOf"/>
    private static readonly UserId _anonymousUser = AlvoContext.Anonymous.User;

    /// <inheritdoc cref="AuthTypeOf"/>
    /// <remarks>
    /// Read off <see cref="AlvoContext.System"/> rather than restated, so the reserved id has one authority:
    /// a second copy of that <see cref="Guid"/> would let the port move it and leave every system-made
    /// change reported as an ordinary caller's.
    /// </remarks>
    private static readonly UserId _systemUser = AlvoContext.System(tenant: null).User;
}

/// <summary>Which of <c>IAlvoData</c>'s three write faces produced an event.</summary>
internal enum OutboxOperation
{
    /// <summary>A row was inserted.</summary>
    Created,

    /// <summary>A row's values moved.</summary>
    Updated,

    /// <summary>A row was removed.</summary>
    Deleted,
}
