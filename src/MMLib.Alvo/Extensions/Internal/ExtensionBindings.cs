using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>One problem a registration has against the primed schema.</summary>
internal sealed record ExtensionProblem(string Id, string Kind, string Locator, string Message, string Fix);

/// <summary>Two or more registrations stating one <c>Order</c> on one target.</summary>
internal sealed record OrderTie(string Target, int Order, IReadOnlyList<string> Ids);

/// <summary>What binding a registry against one catalog produced.</summary>
internal sealed record ExtensionBindingResult(
    ExtensionBindings Bindings, IReadOnlyList<ExtensionProblem> Problems, IReadOnlyList<OrderTie> Ties);

/// <summary>A compiled <c>when</c> and the caller values it reads that an envelope may lack.</summary>
internal sealed record BoundCondition(CompiledExpression? Expression, RequiredContext Required, bool ReadsRoles)
{
    internal static BoundCondition Always { get; } = new(null, RequiredContext.None, false);

    internal static BoundCondition Of(CompiledExpression expression) => new(
        expression,
        new RequiredContext(
            TenantId: PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.TenantId),
            UserId: PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.UserId)),
        PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.UserRoles));
}

/// <summary>A registration bound to one catalog: its condition per entity, or one for every event it selects.</summary>
internal sealed record BoundHandler(
    HandlerRegistration Registration, IReadOnlyDictionary<string, BoundCondition> Conditions, BoundCondition Fallback)
{
    internal string Id => Registration.Id;

    internal HandlerSettings Settings => Registration.Settings;
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
}
