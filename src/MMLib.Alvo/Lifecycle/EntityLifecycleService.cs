using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// The core's <see cref="IEntityLifecycle"/>: reads the compiled lifecycle off the write's own decision, and judges it inside
/// one host-function budget pass.
/// </summary>
/// <param name="budget">The host-function budget a transition's <c>when</c> runs under.</param>
internal sealed class EntityLifecycleService(CelFunctionBudgetPolicy budget) : IEntityLifecycle
{
    /// <inheritdoc />
    public void EnsureTransition(PolicyDecision decision, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext caller)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(caller);
        if (LifecycleOf(decision).StateMachine is not { } machine)
        {
            return;
        }

        using var pass = budget.Enter();
        StateMachineGuard.Ensure(machine, operation, candidate, previous, caller);
    }

    /// <inheritdoc />
    public bool ArmsTimers(PolicyDecision decision) => LifecycleOf(decision).Timers.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<TimerChange> TimerChanges(PolicyDecision decision, EntityTimerWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        _ = LifecycleOf(decision);
        return [];
    }

    private static EntityLifecycle LifecycleOf(PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsDenied)
        {
            throw new InvalidOperationException("A denied decision reached the lifecycle; the write path must refuse it first.");
        }

        return decision.Lifecycle as EntityLifecycle ?? EntityLifecycle.None;
    }
}
