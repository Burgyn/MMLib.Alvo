namespace MMLib.Alvo.Events;

/// <summary>
/// One <b>custom application event</b>, in the only shape <see cref="IOutboxStore.AppendAsync"/> accepts —
/// and the reason the reserved-namespace guard cannot be routed around.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists to make a guarantee structural instead of documentary, and it was earned by a
/// review.</b> The first version of this PR put <c>AlvoEventName.EnsureCustom</c> in the core's
/// <see cref="IAlvoEvents"/> implementation and let <see cref="IOutboxStore.AppendAsync"/> take a bare
/// <see cref="AlvoEvent"/>. Two independent reviews found the same hole: <see cref="IOutboxStore"/> is public
/// and DI-registered, <see cref="AlvoEvent"/> is a public record with public initializers, so a host could
/// resolve the port and append <c>entity.orders.updated</c> with `authtype: system` and a payload of its
/// choosing — firing every after-hook subscribed to the real name, for a row nobody wrote. That is the exact
/// forgery the guard exists to refuse, reachable one layer below it.
/// </para>
/// <para>
/// <b>The fix is the shape, not a second check.</b> A check in the driver would have to be repeated by every
/// other driver and would be silently absent from the one that forgot; a check on the interface cannot be
/// enforced at all. So the port's parameter became a type whose <em>only</em> door runs the guard:
/// <see cref="Create"/>. This is the house rule <see cref="IOutboxStore"/>'s own remarks state for the id —
/// <em>"the wrong implementation is unavailable rather than merely discouraged"</em> — applied to the caller
/// instead of the implementer.
/// </para>
/// <para>
/// <b>The factory is public, and the first draft's internal constructor was wrong.</b> Making it internal
/// looked stronger and was weaker in both directions: it rested on <c>InternalsVisibleTo</c>, which names a
/// published assembly and is forgeable, and it locked out the two callers that legitimately need to build
/// one — a host assembling its own envelope, and <c>OutboxStoreContractTests</c>, the public suite an
/// <em>external</em> driver author inherits to prove their <see cref="IOutboxStore.AppendAsync"/> works. The
/// invariant that actually matters is not "only the framework constructs one" but <b>"none carries a reserved
/// name"</b>, and a public guarded factory holds exactly that, for everyone, with no forgeability caveat.
/// </para>
/// </remarks>
public sealed class AlvoCustomEvent
{
    /// <summary>
    /// The only way to make one: guards <paramref name="envelope"/>'s name, then wraps it.
    /// </summary>
    /// <param name="envelope">The envelope to append.</param>
    /// <returns>The wrapped envelope, guaranteed not to name a reserved namespace.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Its <see cref="AlvoEvent.Type"/> is blank, sits in a reserved namespace, or is not a well-formed event
    /// name — see <see cref="AlvoEventName.EnsureCustom"/>.
    /// </exception>
    public static AlvoCustomEvent Create(AlvoEvent envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        AlvoEventName.EnsureCustom(envelope.Type, nameof(envelope));
        return new AlvoCustomEvent(envelope);
    }

    private AlvoCustomEvent(AlvoEvent envelope) => Envelope = envelope;

    /// <summary>Gets the envelope to append, exactly as the queue must store it.</summary>
    public AlvoEvent Envelope { get; }
}
