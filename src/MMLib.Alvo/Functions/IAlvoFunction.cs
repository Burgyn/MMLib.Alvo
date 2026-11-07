namespace MMLib.Alvo.Functions;

/// <summary>
/// Host code a descriptor calls by name from a <c>function</c> action — an after-hook or an automation rule — once the write
/// that triggered it has committed, as a delivery that is retried and recorded.
/// </summary>
/// <typeparam name="TInput">
/// What the action's <c>input</c> binds to; its exported JSON Schema is what the descriptor's input is checked against when it
/// is applied.
/// </typeparam>
/// <remarks>Register it with <c>AddFunction&lt;TFunction&gt;(name)</c>; it is resolved from a fresh scope per run.</remarks>
public interface IAlvoFunction<in TInput>
    where TInput : class
{
    /// <summary>Runs the function.</summary>
    /// <param name="input">The action's input, bound from its literals and evaluated expressions.</param>
    /// <param name="context">The event that triggered it, the attempt, and data access as the system in the event's tenant.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>A task that completes when the function has run.</returns>
    /// <exception cref="AlvoFunctionSkippedException">The function chose not to act; the delivery ends succeeded.</exception>
    Task InvokeAsync(TInput input, AlvoFunctionContext context, CancellationToken cancellationToken);
}
