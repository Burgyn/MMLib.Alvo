using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// One framework data migration per entity that declares timers, synthesized from the catalog beside the descriptor actions:
/// it re-plans the entity's existing rows (<see cref="EntityTimerReplanJob"/>) the first time a revision carries its timers'
/// definition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its id is the definition.</b> <c>/entities/&lt;entity&gt;/timers#&lt;hash&gt;</c>, the hash over every timer's name, <c>at</c>,
/// <c>when</c> and <c>event</c>; the data migration starter starts an id once and never again, so an apply that adds a timer or
/// changes one starts a new migration, and an apply that leaves them alone starts none. A rollback to a definition already
/// re-planned once starts none either: its id is already started.
/// </para>
/// <para>
/// <b>One run, whatever the tenancy.</b> The re-plan reads every tenant's rows below the policy layer and arms each timer in
/// its row's own tenant, so it spans tenants rather than running once per tenant — and needs no tenant list.
/// </para>
/// </remarks>
internal static class TimerReplanBindings
{
    private const int FirstRevision = 1;
    private const string EventPrefix = "alvo.migration.timers.";

    /// <summary>The bound data migration of every entity of <paramref name="catalog"/> that declares a timer.</summary>
    /// <param name="catalog">The catalog the timers were compiled into.</param>
    /// <param name="timeout">How long one run may take.</param>
    internal static IEnumerable<BoundHandler> Bind(PolicyCatalog catalog, TimeSpan timeout)
    {
        var settings = HandlerSettings.From(new AlvoHandlerOptions()) with { Timeout = timeout };

        return [.. catalog.Entities.Where(entity => entity.Value.Lifecycle.Timers.Count > 0)
            .Select(entity => Bound(entity.Key, entity.Value.Lifecycle.Timers, settings))];
    }

    /// <summary>The event type a re-plan of <paramref name="entity"/> is started by.</summary>
    /// <param name="entity">The entity.</param>
    internal static string EventTypeFor(string entity) => EventPrefix + ExtensionIds.DerivedSegment(entity);

    private static BoundHandler Bound(string entity, IReadOnlyList<CompiledTimer> timers, HandlerSettings settings)
    {
        var registration = new HandlerRegistration(
            DescriptorActionIds.TimerReplan(entity, [.. timers.Select(Definition)]), int.MaxValue, new NamedEvent(EventTypeFor(entity)),
            When: null, settings, HandlerInvoker.ForTimerReplan(entity),
            Job: new JobSpec(DeliveryKind.Migration, null, null, AlvoScheduleScope.Global, FirstRevision, SpansTenants: true));

        return new BoundHandler(registration, new Dictionary<string, BoundCondition>(StringComparer.Ordinal), BoundCondition.Always);
    }

    private static string Definition(CompiledTimer timer) =>
        string.Join('\u001f', timer.Name, timer.At.Source, timer.When?.Source ?? string.Empty, timer.EventType);
}
