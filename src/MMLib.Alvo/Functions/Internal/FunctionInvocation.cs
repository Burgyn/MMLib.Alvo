using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Functions.Internal;

/// <summary>
/// Runs one <c>function</c> action as its delivery: binds the input — literals as written, <c>$cel</c> values evaluated over the
/// event's row images at the event's instant — with the one shared serializer configuration, resolves the function keyed by
/// its name from the delivery's scope, and calls it.
/// </summary>
/// <remarks>
/// An input the type cannot bind is not curable by a retry: the delivery ends dead <c>function-input-invalid</c> after one
/// attempt, and the log line names the function, the subscriber and the event — never a value. A function that throws
/// <see cref="AlvoFunctionSkippedException"/> ends its delivery succeeded.
/// </remarks>
/// <param name="values">Evaluates the input's values over the event.</param>
/// <param name="logger">Where a refused input and a skipped run are recorded.</param>
internal sealed class FunctionInvocation(ActionValues values, ILogger<FunctionInvocation> logger)
{
    private static readonly MethodInfo _call = typeof(FunctionInvocation).GetMethod(nameof(CallAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, Func<object, object, AlvoFunctionContext, CancellationToken, Task>> _calls = new();

    /// <summary>Binds the input, then runs the function in <paramref name="services"/>' scope.</summary>
    /// <exception cref="ActionRefusedException">The input does not bind to the function's input type.</exception>
    internal async Task InvokeAsync(CompiledAction action, AlvoEventContext context, IServiceProvider services, CancellationToken ct)
    {
        var function = action.Function ?? throw new InvalidOperationException($"'{context.Subscriber}' is not a function action.");
        var input = Bind(action, function, context, ct);
        var instance = services.GetRequiredKeyedService(function.Function, function.Id);
        var run = new AlvoFunctionContext(function.Id, context.Event, context.Attempt, context.Data, context.Steps);
        try
        {
            await CallOf(function.Input)(instance, input, run, ct).ConfigureAwait(false);
        }
        catch (AlvoFunctionSkippedException)
        {
            DeliveryLog.FunctionSkipped(logger, function.Id, context.Subscriber, context.EventId);
        }
    }

    private object Bind(CompiledAction action, FunctionRegistration function, AlvoEventContext context, CancellationToken ct)
    {
        try
        {
            return InputOf(values.Evaluate(action, context.Event, ct)).Deserialize(function.Input, FunctionInputSchema.SerializerOptions)
                ?? throw new JsonException("The input bound to null.");
        }
        catch (Exception unbound) when (unbound is JsonException or NotSupportedException or ArgumentException)
        {
            DeliveryLog.FunctionInputInvalid(logger, function.Id, context.Subscriber, context.EventId);
            throw new ActionRefusedException(DeliveryErrors.FunctionInputInvalid);
        }
    }

    /// <summary>The input as a JSON object: each literal as written, each expression's value as the binder reads it.</summary>
    private static JsonObject InputOf(Dictionary<string, object?> values)
    {
        var input = new JsonObject();
        foreach (var (name, value) in values)
        {
            input[name] = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), FunctionInputSchema.SerializerOptions);
        }

        return input;
    }

    private static Func<object, object, AlvoFunctionContext, CancellationToken, Task> CallOf(Type input) =>
        _calls.GetOrAdd(input, type => _call.MakeGenericMethod(type)
            .CreateDelegate<Func<object, object, AlvoFunctionContext, CancellationToken, Task>>());

    private static Task CallAsync<TInput>(object function, object input, AlvoFunctionContext context, CancellationToken ct)
        where TInput : class =>
        ((IAlvoFunction<TInput>)function).InvokeAsync((TInput)input, context, ct);
}
