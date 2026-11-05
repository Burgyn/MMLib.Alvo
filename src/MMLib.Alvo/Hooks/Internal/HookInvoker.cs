using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Extensions.Internal;
using System.Reflection;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// How one hook is called (Ruling 21): a class built per run from the hook's scope — or resolved, when registered — or a
/// delegate whose parameters bind to the context, the token or a service. Its direct dependencies are kept for the boot ban.
/// </summary>
internal sealed class HookInvoker
{
    private readonly Func<IServiceProvider, HookRun, CancellationToken, ValueTask> _invoke;
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private HookInvoker(
        string? typeName,
        IReadOnlyList<(string Name, Type Type)> dependencies,
        Func<IServiceProvider, HookRun, CancellationToken, ValueTask> invoke,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        TypeName = typeName;
        Dependencies = dependencies;
        _invoke = invoke;
        _unresolvable = unresolvable;
    }

    /// <summary>Gets the hook class's full name; <see langword="null"/> for a delegate.</summary>
    internal string? TypeName { get; }

    /// <summary>Gets every constructor parameter of a class hook, or every non-supplied parameter of a delegate.</summary>
    internal IReadOnlyList<(string Name, Type Type)> Dependencies { get; }

    internal ValueTask InvokeAsync(IServiceProvider services, HookRun run, CancellationToken cancellationToken) => _invoke(services, run, cancellationToken);

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static HookInvoker FromClass<THook, TContext>(Func<HookRun, TContext> context, Func<THook, TContext, CancellationToken, ValueTask> call)
        where THook : class
    {
        if (typeof(THook).IsAbstract)
        {
            throw new ArgumentException($"{typeof(THook).Name} is abstract, so Alvo cannot build it for a write; register a concrete hook class.", nameof(THook));
        }

        return new(
            typeof(THook).FullName,
            [.. typeof(THook).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => (parameter.Name ?? "?", parameter.ParameterType)).Distinct()],
            (services, run, ct) => InvokeClassAsync(services, context(run), call, ct),
            isService => HandlerInvoker.ClassUnresolvable(typeof(THook), isService));
    }

    internal static HookInvoker FromDelegate<TContext>(Delegate hook, Func<HookRun, TContext> context)
    {
        ArgumentNullException.ThrowIfNull(hook);
        EnsureReturnsAwaitable(hook);
        EnsureOnlyItsOwnContext(hook, typeof(TContext));
        var parameters = hook.Method.GetParameters();
        var dependencies = parameters.Where(parameter => !IsSupplied(parameter.ParameterType, typeof(TContext))).ToList();

        return new(
            typeName: null,
            [.. dependencies.Select(parameter => (parameter.Name ?? "?", parameter.ParameterType))],
            (services, run, ct) => InvokeDelegateAsync(hook, parameters, services, context(run)!, ct),
            isService => [.. dependencies.Where(parameter => !parameter.HasDefaultValue).Select(parameter => parameter.ParameterType).Where(type => !isService.IsService(type))]);
    }

    private static async ValueTask InvokeClassAsync<THook, TContext>(
        IServiceProvider services, TContext context, Func<THook, TContext, CancellationToken, ValueTask> call, CancellationToken ct)
        where THook : class
    {
        var registered = services.GetService<THook>();
        var hook = registered ?? ActivatorUtilities.CreateInstance<THook>(services);
        try
        {
            await call(hook, context, ct).ConfigureAwait(false);
        }
        finally
        {
            if (registered is null)
            {
                await InvokerCalls.DisposeAsync(hook).ConfigureAwait(false);
            }
        }
    }

    private static ValueTask InvokeDelegateAsync(Delegate hook, ParameterInfo[] parameters, IServiceProvider services, object context, CancellationToken ct) =>
        InvokerCalls.InvokeAsync(hook, [.. parameters.Select(parameter => Argument(parameter, services, context, ct))]);

    private static object? Argument(ParameterInfo parameter, IServiceProvider services, object context, CancellationToken ct) =>
        parameter.ParameterType == typeof(CancellationToken) ? ct
        : IsSupplied(parameter.ParameterType, context.GetType()) ? context
        : parameter.HasDefaultValue ? services.GetService(parameter.ParameterType) ?? parameter.DefaultValue
        : services.GetRequiredService(parameter.ParameterType);

    private static void EnsureReturnsAwaitable(Delegate hook)
    {
        var method = hook.Method;
        if (InvokerCalls.IsAsyncVoid(method))
        {
            throw new ArgumentException($"Hook '{method.Name}' is async void: Alvo could not wait for it, so the write would go on before it ends. Make it return ValueTask.", nameof(hook));
        }

        if (!InvokerCalls.ReturnsAwaitableOrNothing(method))
        {
            throw new ArgumentException($"A hook delegate returns ValueTask, Task or nothing; this one returns {method.ReturnType.Name}, whose result Alvo would discard.", nameof(hook));
        }
    }

    private static void EnsureOnlyItsOwnContext(Delegate hook, Type context)
    {
        var other = hook.Method.GetParameters().FirstOrDefault(parameter => IsHookContext(parameter.ParameterType) && parameter.ParameterType != context);
        if (other is not null)
        {
            throw new ArgumentException(
                $"This registration hands its delegate {InvokerCalls.Display(context)}; parameter '{other.Name}' asks for {InvokerCalls.Display(other.ParameterType)}, which it would never be given.",
                nameof(hook));
        }
    }

    private static bool IsSupplied(Type type, Type context) => type == context || type == typeof(CancellationToken);

    private static bool IsHookContext(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() is var definition
        && (definition == typeof(ValidateWrite<>) || definition == typeof(BeforeWrite<>) || definition == typeof(AfterWrite<>)
            || definition == typeof(BeforeDelete<>) || definition == typeof(AfterDelete<>));

}
