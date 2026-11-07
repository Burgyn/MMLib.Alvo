using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Every descriptor action of one catalog — each after-hook action and each automation rule action — as a synthetic bound
/// handler, so fan-out, claim, retry, lanes, dead-lettering and the Operations page treat it exactly like a host handler. A cron
/// rule adds one trigger-only schedule that mints its event and is never delivered to.
/// </summary>
/// <remarks>
/// They come from the catalog, never the registry: they are not catalog entries, carry default settings — a timeout, per-record
/// order, the default retry, run as the system in the event's tenant — and their subscriber ids are
/// <see cref="DescriptorActionIds"/>'.
/// </remarks>
internal static class DescriptorActionBindings
{
    private const string CronZone = "UTC";

    /// <summary>The bound handlers of every descriptor action of <paramref name="catalog"/>.</summary>
    /// <param name="catalog">The catalog the actions were compiled into.</param>
    /// <param name="actionTimeout">How long one action may run.</param>
    internal static IEnumerable<BoundHandler> Bind(PolicyCatalog catalog, TimeSpan actionTimeout)
    {
        var settings = HandlerSettings.From(new AlvoHandlerOptions()) with { Timeout = actionTimeout };

        return [.. AfterHooks(catalog, settings), .. catalog.Automation.SelectMany(rule => Rule(rule, settings))];
    }

    private static IEnumerable<BoundHandler> AfterHooks(PolicyCatalog catalog, HandlerSettings settings) =>
        from entity in catalog.Entities
        from point in entity.Value.AfterHooks.ByVerb()
        from hook in point.Hooks
        select Bound(hook.SubscriberId, new EntityEvents([entity.Key], point.Verb), entity.Key, hook.Condition, hook.Action, settings);

    private static IEnumerable<BoundHandler> Rule(CompiledAutomationRule rule, HandlerSettings settings) => rule.Trigger switch
    {
        EventTrigger trigger => rule.Actions.Select(action =>
            Bound(action.SubscriberId, new EntityEvents([trigger.Entity], trigger.Verb), trigger.Entity, rule.Condition, action.Action, settings)),
        CronTrigger cron => [Schedule(cron, settings), .. rule.Actions.Select(action =>
            Bound(action.SubscriberId, new NamedEvent(cron.EventType), entity: null, condition: null, action.Action, settings))],
        _ => [],
    };

    private static BoundHandler Schedule(CronTrigger cron, HandlerSettings settings)
    {
        var scope = cron.PerTenant ? AlvoScheduleScope.EachTenant : AlvoScheduleScope.Global;
        var registration = new HandlerRegistration(
            cron.SubscriberId, int.MaxValue, new NamedEvent(cron.EventType), When: null, settings, HandlerInvoker.TriggerOnly,
            Job: new JobSpec(DeliveryKind.Schedule, cron.Cron, CronZone, scope, AfterRevision: null), TriggerOnly: true);

        return new BoundHandler(registration, new Dictionary<string, BoundCondition>(StringComparer.Ordinal), BoundCondition.Always);
    }

    private static BoundHandler Bound(
        string id, HandlerSource source, string? entity, CompiledExpression? condition, CompiledAction action, HandlerSettings settings)
    {
        var registration = new HandlerRegistration(
            id, int.MaxValue, source, When: null, settings, HandlerInvoker.ForAction(action),
            Kind: action.Action is FunctionAction ? DeliveryKind.Function : DeliveryKind.Action);
        var bound = condition is null ? BoundCondition.Always : BoundCondition.Of(condition);
        var conditions = new Dictionary<string, BoundCondition>(StringComparer.Ordinal);
        if (entity is not null)
        {
            conditions[entity] = bound;
        }

        return new BoundHandler(registration, conditions, bound);
    }
}
