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
        EnsureOwnPartition(envelope);
        return new AlvoCustomEvent(envelope);
    }

    /// <summary>
    /// Refuses an envelope whose partition key is not its own, so a custom event cannot be ordered into a
    /// data entity's partition.
    /// </summary>
    /// <param name="envelope">The envelope, whose name is already guarded.</param>
    /// <remarks>
    /// <para>
    /// <b>The second half of the guard, and it exists because the documentation over-claimed without it.</b>
    /// <c>AlvoEvents.PartitionKeyFor</c> says the disjointness from a data event's <c>{entity}:{rowId}</c> is
    /// <em>"provable, not probable … closed by shape rather than by a warning"</em> — but that was only true
    /// for callers coming through <c>IAlvoEvents.PublishAsync</c>, and this type exists precisely so that is
    /// not the only door. A host could <c>Create</c> a well-named <c>crm.thing.happened</c> carrying
    /// <c>PartitionKey = "deals:&lt;rowId&gt;"</c> and land inside a real entity's partition the day F7's
    /// partitioned claim (<b>#150</b>) reads the column. Found by review, and it is the same lesson as the
    /// first bypass: a guarantee is only as strong as the narrowest door that can reach it.
    /// </para>
    /// <para>
    /// <b>The prefix, not the whole key.</b> Requiring <c>{type}:</c> is what makes the disjointness hold —
    /// an entity name cannot contain a dot (<c>schema/project.schema.json</c>, <c>entities</c>'
    /// <c>propertyNames</c>) and a custom type must contain at least one — while leaving a host free to choose
    /// what it orders by after it.
    /// </para>
    /// </remarks>
    private static void EnsureOwnPartition(AlvoEvent envelope)
    {
        var required = envelope.Type + PartitionSeparator;
        if (envelope.PartitionKey.StartsWith(required, StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentException(
            $"Custom event '{envelope.Type}' carries partition key '{envelope.PartitionKey}', which is not in "
            + $"its own partition. A custom event's key must start with '{required}' so it cannot be ordered "
            + "into a data entity's partition — those are keyed '{entity}:{rowId}', and an entity name carries "
            + $"no dot while an event name must. Use '{required}{{whatever you order by}}', e.g. "
            + $"'{required}orders/42'.",
            nameof(envelope));
    }

    /// <summary>The separator between an event's own namespace and what it is about, in a partition key.</summary>
    private const char PartitionSeparator = ':';

    private AlvoCustomEvent(AlvoEvent envelope) => Envelope = envelope;

    /// <summary>Gets the envelope to append, exactly as the queue must store it.</summary>
    public AlvoEvent Envelope { get; }
}
