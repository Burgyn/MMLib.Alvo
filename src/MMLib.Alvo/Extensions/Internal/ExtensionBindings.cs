using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>One problem a registration has against the primed schema.</summary>
internal sealed record ExtensionProblem(string Id, string Kind, string Locator, string Message, string Fix);

/// <summary>Two or more registrations stating one <c>Order</c> on one target.</summary>
internal sealed record OrderTie(string Target, int Order, IReadOnlyList<string> Ids);

/// <summary>What checking a registry without any catalog produced: the problems no descriptor can cure, and the ties.</summary>
internal sealed record ExtensionRegistrationResult(IReadOnlyList<ExtensionProblem> Problems, IReadOnlyList<OrderTie> Ties);

/// <summary>What binding a registry against one catalog produced.</summary>
internal sealed record ExtensionBindingResult(
    ExtensionBindings Bindings, IReadOnlyList<ExtensionProblem> Problems, IReadOnlyList<OrderTie> Ties);

/// <summary>A compiled <c>when</c> and the caller values it reads that an envelope may lack.</summary>
internal sealed record BoundCondition(CompiledExpression? Expression, RequiredContext Required, bool ReadsRoles)
{
    internal static BoundCondition Always { get; } = new(null, RequiredContext.None, false);

    internal static BoundCondition Of(CompiledExpression expression) =>
        new(expression, EventConditionReads.Required(expression), EventConditionReads.ReadsRoles(expression));
}

/// <summary>A registration bound to one catalog: its condition per entity, or one for every event it selects.</summary>
internal sealed record BoundHandler(
    HandlerRegistration Registration, IReadOnlyDictionary<string, BoundCondition> Conditions, BoundCondition Fallback)
{
    internal string Id => Registration.Id;

    internal HandlerSettings Settings => Registration.Settings;

    /// <summary>The condition this handler judges <paramref name="event"/> by, or <see langword="null"/> when its source does not select it.</summary>
    internal BoundCondition? Selects(AlvoEvent @event) => HandlerSources.Selects(Registration.Source, @event.Type) switch
    {
        (false, _) => null,
        (true, { } entity) when Registration.Source is EntityEvents { IsAll: false } => Conditions.GetValueOrDefault(entity),
        _ => Fallback,
    };
}

/// <summary>Every handler bound to one catalog, by <c>Order</c> and then registration order.</summary>
internal sealed class ExtensionBindings
{
    private readonly Dictionary<string, BoundHandler> _byId;

    internal ExtensionBindings(IEnumerable<BoundHandler> handlers, IEnumerable<HandlerRegistration>? unbound = null)
    {
        Unbound = [.. unbound ?? []];
        Handlers = [.. handlers.OrderBy(handler => handler.Settings.Order).ThenBy(handler => handler.Registration.Sequence)];
        _byId = Handlers.ToDictionary(handler => handler.Id, StringComparer.Ordinal);
    }

    internal static ExtensionBindings None { get; } = new([]);

    internal IReadOnlyList<BoundHandler> Handlers { get; }

    /// <summary>The registered handlers that do not fit this catalog; the fan-out records their events dead (<c>unbound</c>).</summary>
    internal IReadOnlyList<HandlerRegistration> Unbound { get; }

    internal TimeSpan LongestTimeout => Handlers.Count == 0 ? TimeSpan.Zero : Handlers.Max(handler => handler.Settings.Timeout);

    internal BoundHandler? Handler(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The bound handlers whose source selects <paramref name="event"/>, in order, each with the condition to judge.</summary>
    internal IEnumerable<(BoundHandler Handler, BoundCondition Condition)> Selecting(AlvoEvent @event)
    {
        foreach (var handler in Handlers)
        {
            if (handler.Selects(@event) is { } condition)
            {
                yield return (handler, condition);
            }
        }
    }

    /// <summary>The unbound registrations whose source selects <paramref name="event"/>; no <c>when</c> is judged.</summary>
    internal IEnumerable<HandlerRegistration> UnboundSelecting(AlvoEvent @event) =>
        Unbound.Where(handler => HandlerSources.Selects(handler.Source, @event.Type).Selected);
}
