using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// Publishes a host's own <b>custom application events</b> onto the same durable queue Alvo's data events
/// travel on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the publish half of publish–subscribe, and today it has no subscribe half.</b>
/// <c>$defs/eventPattern</c> — the frozen grammar every descriptor subscription is typed by — admits only the
/// three namespaces <see cref="PublishAsync"/> refuses, so a name this API accepts is a name no automation
/// rule and no after-hook can name. A published event is therefore durable, ordered and inspectable in the
/// outbox, and delivered to nothing: the dispatcher claims it, matches no subscription, counts it as filtered
/// and marks it dispatched. That is deliberate and recorded (<c>docs/architecture/events.md</c>); the
/// namespace that makes a custom event subscribable is a design decision taken once, not a prefix added under
/// one PR's schedule.
/// </para>
/// <para>
/// <b>Why the guarantee ships before the feature it guards.</b> The refusal costs nothing now and cannot be
/// added later without breaking whichever host is already minting <c>entity.orders.updated</c> by then. A
/// forged data event is not a cosmetic problem: every rule and hook subscribing to the real name would fire
/// on it, carrying a partition key and provenance for a record nobody wrote.
/// </para>
/// <para>
/// <b>Not transactional with anything.</b> The spec's "the event is published in the same transaction as the
/// data change" is a guarantee about a <em>data</em> change, and a custom event has none — this appends with
/// one autocommit statement. A host that needs its own write and its own event to commit together cannot get
/// that here.
/// </para>
/// </remarks>
public interface IAlvoEvents
{
    /// <summary>Publishes one custom application event.</summary>
    /// <param name="type">
    /// The event's name: two or more dot-separated lower-case segments, e.g. <c>orders.approved</c>.
    /// <b>Never</b> in the <c>entity.</c>, <c>auth.</c> or <c>storage.</c> namespaces — those are Alvo's own,
    /// and a name in one is refused rather than published.
    /// </param>
    /// <param name="subject">
    /// What the event is about, in the host's own vocabulary (e.g. <c>orders/42</c>). It is also the event's
    /// partition key, because it is the only thing here that identifies the subject two events might share —
    /// so per-subject ordering is the only ordering a custom event can be given.
    /// </param>
    /// <param name="data">
    /// The event's payload, or <see langword="null"/> for an event that carries none. <b>Values must be
    /// scalars the envelope can express</b> — <see cref="string"/>, <see cref="bool"/>, <see cref="Guid"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="DateTime"/>, <see cref="DateOnly"/>, the numeric types, or
    /// <see langword="null"/>. A nested dictionary, an array, a <c>JsonElement</c> or any other type is
    /// refused: the envelope is a flat record on the wire, so there is nothing for a nested value to become.
    /// Flatten it, or serialize it to a string yourself.
    /// </param>
    /// <param name="context">The caller publishing it, recorded as the event's provenance. Never ambient.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is blank, sits in a reserved namespace, or is not a well-formed event name;
    /// or <paramref name="data"/> carries a value the envelope cannot express.
    /// </exception>
    Task PublishAsync(
        string type,
        string subject,
        IReadOnlyDictionary<string, object?>? data,
        AlvoContext context,
        CancellationToken cancellationToken = default);
}
