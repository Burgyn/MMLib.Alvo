using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>The framework symbols one compilation resolves, read once at its start.</summary>
internal sealed class AlvoSymbols
{
    private static readonly ImmutableDictionary<string, string> _hookStages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MMLib.Alvo.Hooks.IBeforeWriteHook"] = "before-write",
        ["MMLib.Alvo.Hooks.IBeforeWriteHook`1"] = "before-write",
        ["MMLib.Alvo.Hooks.IAfterWriteHook"] = "after-write",
        ["MMLib.Alvo.Hooks.IAfterWriteHook`1"] = "after-write",
        ["MMLib.Alvo.Hooks.IBeforeDeleteHook"] = "before-delete",
        ["MMLib.Alvo.Hooks.IBeforeDeleteHook`1"] = "before-delete",
        ["MMLib.Alvo.Hooks.IAfterDeleteHook"] = "after-delete",
        ["MMLib.Alvo.Hooks.IAfterDeleteHook`1"] = "after-delete",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> _delegateVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BeforeWrite"] = "before-write",
        ["AfterWrite"] = "after-write",
        ["BeforeDelete"] = "before-delete",
        ["AfterDelete"] = "after-delete",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private readonly ImmutableDictionary<INamedTypeSymbol, string> _hooks;
    private readonly INamedTypeSymbol? _celHost;
    private readonly INamedTypeSymbol? _entityBuilder;
    private readonly INamedTypeSymbol? _typedEntityBuilder;
    private readonly INamedTypeSymbol? _session;
    private readonly INamedTypeSymbol? _scheduler;
    private readonly INamedTypeSymbol? _schedulerExtensions;

    private AlvoSymbols(Compilation compilation, ImmutableDictionary<INamedTypeSymbol, string> hooks)
    {
        _hooks = hooks;
        _celHost = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.AlvoBuilderExtensions");
        _entityBuilder = compilation.GetTypeByMetadataName("MMLib.Alvo.Extensions.AlvoEntityBuilder");
        _typedEntityBuilder = compilation.GetTypeByMetadataName("MMLib.Alvo.Extensions.AlvoEntityBuilder`1");
        _session = compilation.GetTypeByMetadataName("MMLib.Alvo.Data.IAlvoSession");
        _scheduler = compilation.GetTypeByMetadataName("MMLib.Alvo.Scheduling.IAlvoScheduler");
        _schedulerExtensions = compilation.GetTypeByMetadataName("MMLib.Alvo.Scheduling.AlvoSchedulerExtensions");
    }

    /// <summary>The symbols of <paramref name="compilation"/>, or <see langword="null"/> when it does not reference Alvo.</summary>
    internal static AlvoSymbols? From(Compilation compilation)
    {
        var hooks = _hookStages
            .Select(pair => (Type: compilation.GetTypeByMetadataName(pair.Key), Stage: pair.Value))
            .Where(pair => pair.Type is not null)
            .ToImmutableDictionary(pair => pair.Type!, pair => pair.Stage, (IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
        return hooks.IsEmpty ? null : new AlvoSymbols(compilation, hooks);
    }

    /// <summary>
    /// The scope of code declared in <paramref name="type"/>: the nearest type in its containing-type chain that implements
    /// an in-transaction hook contract, so a helper type nested in a hook is the hook's code.
    /// </summary>
    internal AnalysisScope? HookScopeOf(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            foreach (var contract in current.AllInterfaces)
            {
                if (_hooks.TryGetValue(contract.OriginalDefinition, out var stage))
                {
                    return AnalysisScope.OfHookType(stage, current);
                }
            }
        }

        return null;
    }

    /// <summary>The scope of a registration call's delegate, and that delegate, when <paramref name="invocation"/> registers one.</summary>
    internal AnalysisScope? RegistrationOf(IInvocationOperation invocation, out IOperation? registered)
    {
        registered = null;
        var method = invocation.TargetMethod;
        var container = method.ContainingType?.OriginalDefinition;
        if (Same(container, _celHost) && method.Name == "AddCelFunction")
        {
            registered = Argument(invocation, "function");
            return AnalysisScope.CelFunction;
        }

        if ((Same(container, _entityBuilder) || Same(container, _typedEntityBuilder)) && _delegateVerbs.TryGetValue(method.Name, out var stage))
        {
            registered = Argument(invocation, "hook");
            return registered is null ? null : AnalysisScope.HookDelegate(stage);
        }

        return null;
    }

    /// <summary>Whether awaiting a call to <paramref name="method"/> is the write's own transaction, the scheduler (which joins it), or the hook's own code.</summary>
    internal bool IsTransactionCall(IMethodSymbol method, AnalysisScope scope) =>
        scope.IsOwnCode(method)
        || IsSession(method.ContainingType)
        || (method.IsExtensionMethod && method.Parameters.Length > 0 && IsSession(method.Parameters[0].Type))
        || method.ContainingType?.ContainingNamespace?.ToDisplayString() == "MMLib.Alvo.Hooks"
        || IsScheduler(method);

    private bool IsScheduler(IMethodSymbol method) =>
        (_schedulerExtensions is not null && Same(method.ContainingType?.OriginalDefinition, _schedulerExtensions))
        || (_scheduler is not null && method.ContainingType is { } type
            && (Same(type.OriginalDefinition, _scheduler) || type.AllInterfaces.Any(contract => Same(contract, _scheduler))));

    private bool IsSession(ITypeSymbol? type) =>
        _session is not null && type is not null
        && (Same(type.OriginalDefinition, _session) || type.AllInterfaces.Any(contract => Same(contract, _session)));

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) =>
        invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameter)?.Value;

    private static bool Same(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);
}
