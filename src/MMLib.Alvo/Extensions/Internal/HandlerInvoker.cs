using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

using System.Reflection;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// How one handler is called: a delegate whose parameters bind to the message, the context, the token or a service, or a
/// class built per delivery from the delivery's scope.
/// </summary>
internal sealed class HandlerInvoker
{
    private static readonly Type[] _messageDefinitions =
        [typeof(AlvoCreated<>), typeof(AlvoUpdated<>), typeof(AlvoDeleted<>), typeof(AlvoSaved<>)];

    private readonly Func<IServiceProvider, object, AlvoEventContext, CancellationToken, Task> _invoke;
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private HandlerInvoker(
        Type messageType,
        string? typeName,
        Func<IServiceProvider, object, AlvoEventContext, CancellationToken, Task> invoke,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        MessageType = messageType;
        TypeName = typeName;
        _invoke = invoke;
        _unresolvable = unresolvable;
    }

    internal Type MessageType { get; }

    /// <summary>The full name of the handler class, or <see langword="null"/> for a delegate, which has none worth naming.</summary>
    internal string? TypeName { get; }

    internal Task InvokeAsync(IServiceProvider services, object message, AlvoEventContext context, CancellationToken cancellationToken) =>
        _invoke(services, message, context, cancellationToken);

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static HandlerInvoker FromDelegate(Delegate handler, Type messageType)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureReturnsTask(handler);
        EnsureOnlyItsOwnMessage(handler, messageType);
        var parameters = handler.Method.GetParameters();
        IReadOnlyList<Type> services =
            [.. parameters.Where(parameter => !parameter.HasDefaultValue).Select(parameter => parameter.ParameterType).Where(type => !IsSupplied(type, messageType))];

        return new HandlerInvoker(
            messageType,
            typeName: null,
            (provider, message, context, ct) => InvokeDelegateAsync(handler, parameters, provider, message, context, ct),
            isService => [.. services.Where(type => !isService.IsService(type))]);
    }

    internal static HandlerInvoker FromClass<THandler, TMessage>()
        where THandler : class, IAlvoEventHandler<TMessage>
        where TMessage : class
    {
        if (typeof(THandler).IsAbstract)
        {
            throw new ArgumentException(
                $"{Display(typeof(THandler))} is abstract, so Alvo cannot build it for a delivery; register a concrete handler class.",
                nameof(THandler));
        }

        return new(typeof(TMessage), typeof(THandler).FullName, InvokeClassAsync<THandler, TMessage>, isService => ClassUnresolvable(typeof(THandler), isService));
    }

    /// <summary>A scheduled job or a data migration, built per run from the delivery's scope; its message is the run's payload.</summary>
    internal static HandlerInvoker ForJob<TJob>()
        where TJob : class
    {
        if (typeof(TJob).IsAbstract)
        {
            throw new ArgumentException(
                $"{Display(typeof(TJob))} is abstract, so Alvo cannot build it for a run; register a concrete job class.", nameof(TJob));
        }

        return new(typeof(AlvoRecord), typeof(TJob).FullName, JobInvocation.InvokeAsync<TJob>, isService => ClassUnresolvable(typeof(TJob), isService));
    }

    /// <summary>A schedule that only mints its event: no delivery is ever recorded for it, so nothing calls this.</summary>
    internal static HandlerInvoker TriggerOnly { get; } = new(typeof(AlvoRecord), typeName: null, (_, _, _, _) => Task.CompletedTask, _ => []);

    /// <summary>A descriptor action, run by <see cref="DescriptorActionRunner"/> from the delivery's scope; it reads no message.</summary>
    internal static HandlerInvoker ForAction(CompiledAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return new(
            typeof(AlvoRecord),
            typeName: null,
            (services, _, context, ct) => services.GetRequiredService<DescriptorActionRunner>().RunAsync(action, context, services, ct),
            _ => []);
    }

    private static void EnsureReturnsTask(Delegate handler)
    {
        var method = handler.Method;
        if (InvokerCalls.IsAsyncVoid(method))
        {
            throw new ArgumentException(
                $"Handler '{method.Name}' is async void: Alvo could not wait for it or see it fail, so the delivery would "
                + "succeed before the work ends. Make it return Task.",
                nameof(handler));
        }

        if (InvokerCalls.ReturnsAwaitableOrNothing(method))
        {
            return;
        }

        throw new ArgumentException(
            $"A handler delegate returns Task, ValueTask or nothing; this one returns {Display(method.ReturnType)}, whose result Alvo "
            + "would discard.",
            nameof(handler));
    }

    private static void EnsureOnlyItsOwnMessage(Delegate handler, Type messageType)
    {
        var other = handler.Method.GetParameters().FirstOrDefault(parameter => IsMessage(parameter.ParameterType) && parameter.ParameterType != messageType);
        if (other is not null)
        {
            throw new ArgumentException(
                $"This registration hands its delegate {Display(messageType)}; parameter '{other.Name}' asks for "
                + $"{Display(other.ParameterType)}, which it would never be given.",
                nameof(handler));
        }
    }

    private static bool IsSupplied(Type type, Type messageType) =>
        type == messageType || type == typeof(AlvoEventContext) || type == typeof(CancellationToken);

    private static bool IsMessage(Type type) =>
        type == typeof(AlvoRecord) || (type.IsGenericType && _messageDefinitions.Contains(type.GetGenericTypeDefinition()));

    private static string Display(Type type) => InvokerCalls.Display(type);

    private static Task InvokeDelegateAsync(
        Delegate handler, ParameterInfo[] parameters, IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct) =>
        InvokerCalls.InvokeAsync(handler, [.. parameters.Select(parameter => Argument(parameter, services, message, context, ct))]).AsTask();

    private static object? Argument(
        ParameterInfo parameter, IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct)
    {
        var type = parameter.ParameterType;

        return type == typeof(CancellationToken) ? ct
            : type == typeof(AlvoEventContext) ? context
            : type.IsInstanceOfType(message) && IsMessage(type) ? message
            : parameter.HasDefaultValue ? services.GetService(type) ?? parameter.DefaultValue
            : services.GetRequiredService(type);
    }

    private static async Task InvokeClassAsync<THandler, TMessage>(
        IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct)
        where THandler : class, IAlvoEventHandler<TMessage>
        where TMessage : class
    {
        var registered = services.GetService<THandler>();
        var handler = registered ?? ActivatorUtilities.CreateInstance<THandler>(services);
        try
        {
            await handler.HandleAsync((TMessage)message, context, ct).ConfigureAwait(false);
        }
        finally
        {
            if (registered is null)
            {
                await InvokerCalls.DisposeAsync(handler).ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<Type> ClassUnresolvable(Type handler, IServiceProviderIsService isService)
    {
        if (isService.IsService(handler))
        {
            return [];
        }

        var constructors = handler.GetConstructors();
        var marked = constructors.FirstOrDefault(constructor => constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false));
        if (marked is not null)
        {
            return Missing(marked, isService);
        }

        var candidates = constructors.OrderByDescending(constructor => constructor.GetParameters().Length).ToList();

        return candidates.Count == 0 ? [handler]
            : candidates.Any(constructor => Missing(constructor, isService).Count == 0) ? []
            : Missing(candidates[0], isService);
    }

    private static IReadOnlyList<Type> Missing(ConstructorInfo constructor, IServiceProviderIsService isService) =>
        [.. constructor.GetParameters()
            .Where(parameter => !parameter.HasDefaultValue && !isService.IsService(parameter.ParameterType))
            .Select(parameter => parameter.ParameterType)];
}
