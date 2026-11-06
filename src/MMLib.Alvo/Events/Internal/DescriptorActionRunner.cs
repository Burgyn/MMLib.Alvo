using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Runs one descriptor action as the body of its own delivery: a <c>webhook</c> or <c>email</c> through
/// <see cref="EventActionExecutor"/>, a <c>function</c> through <see cref="FunctionInvocation"/>, an <c>entity.update</c> through
/// <see cref="EntityUpdateEffect"/>. Whether it runs, its retries, its lane and its dead letter are the delivery engine's.
/// </summary>
/// <param name="executor">The webhook and email arm.</param>
/// <param name="functions">The function arm.</param>
/// <param name="updates">The entity.update arm.</param>
internal sealed class DescriptorActionRunner(EventActionExecutor executor, FunctionInvocation functions, EntityUpdateEffect updates)
{
    /// <summary>Runs <paramref name="action"/> for the delivery <paramref name="context"/> describes.</summary>
    /// <param name="action">The compiled action.</param>
    /// <param name="context">The delivery: the event, the subscriber id, the attempt, data access as the system in the event's tenant.</param>
    /// <param name="services">The delivery's scope.</param>
    /// <param name="ct">The delivery's token, cancelled at its timeout or a shutdown.</param>
    internal Task RunAsync(CompiledAction action, AlvoEventContext context, IServiceProvider services, CancellationToken ct) =>
        action.Action switch
        {
            FunctionAction => functions.InvokeAsync(action, context, services, ct),
            EntityUpdateAction => updates.ApplyAsync(action, context, ct),
            _ => executor.ExecuteAsync(action, context.Subscriber, context.Event, ct),
        };
}
