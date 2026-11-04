using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>What calling host code by reflection takes, shared by handlers and write hooks: the call, its await, the dispose.</summary>
internal static class InvokerCalls
{
    /// <summary>Calls <paramref name="target"/> and awaits the <see cref="Task"/> or <see cref="ValueTask"/> it returns, if any.</summary>
    internal static async ValueTask InvokeAsync(Delegate target, object?[] arguments)
    {
        switch (Invoke(target, arguments))
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;
        }
    }

    internal static bool IsAsyncVoid(MethodInfo method) =>
        method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false);

    internal static bool ReturnsAwaitableOrNothing(MethodInfo method) =>
        method.ReturnType == typeof(void) || method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask);

    internal static string Display(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>" : type.Name;

    internal static ValueTask DisposeAsync(object instance)
    {
        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }

    /// <summary>The host's own exception surfaces as itself, not wrapped in a <see cref="TargetInvocationException"/>.</summary>
    private static object? Invoke(Delegate target, object?[] arguments)
    {
        try
        {
            return target.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
            throw;
        }
    }
}
