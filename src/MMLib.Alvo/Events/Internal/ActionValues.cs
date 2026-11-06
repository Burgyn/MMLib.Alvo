using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Evaluates a descriptor action's values — a function's input, an <c>entity.update</c>'s payload — over the triggering event:
/// a literal as written, a <c>$cel</c> expression over the event's row images at the event's instant, inside one host-function
/// budget pass.
/// </summary>
/// <param name="budget">The budget every host function a value calls runs under.</param>
internal sealed class ActionValues(CelFunctionBudgetPolicy budget)
{
    /// <summary>Each field's value, by name: a literal's <see cref="System.Text.Json.JsonElement"/>, an expression's CLR value.</summary>
    /// <param name="action">The compiled action whose <see cref="CompiledAction.Fields"/> are evaluated.</param>
    /// <param name="event">The event whose images the expressions read.</param>
    /// <param name="ct">Cancels a host function past the budget.</param>
    internal Dictionary<string, object?> Evaluate(CompiledAction action, AlvoEvent @event, CancellationToken ct)
    {
        using var pass = budget.Enter(ct);
        using var instant = AlvoFlow.EnterInstant(@event.Time);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in action.Fields)
        {
            values[name] = value.Expression is { } expression
                ? CelInterpreter.EvaluateMutation(expression, @event.Data.Record ?? AlvoRecord.Empty, @event.Data.OldRecord, @event.Time)
                : value.Literal;
        }

        return values;
    }
}
