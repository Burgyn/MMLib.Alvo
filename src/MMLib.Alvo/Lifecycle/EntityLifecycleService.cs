using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Lifecycle;

/// <summary>
/// The core's <see cref="IEntityLifecycle"/>: reads the compiled lifecycle off the write's own decision, and judges it inside
/// one host-function budget pass.
/// </summary>
/// <param name="budget">The host-function budget a transition's <c>when</c> and a timer's <c>at</c> and <c>when</c> run under.</param>
/// <param name="depth">The chain-depth limit an armed timer's context must be within.</param>
internal sealed class EntityLifecycleService(CelFunctionBudgetPolicy budget, AlvoChainDepthLimit depth) : IEntityLifecycle
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
        if (LifecycleOf(decision).Timers is not { Count: > 0 } timers)
        {
            return [];
        }

        using var pass = budget.Enter();
        return EntityTimerPlanner.Plan(timers, write, depth);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Scope, string Key)> TimerKeys(PolicyDecision decision, string entity, AlvoRecord row) =>
        EntityTimerPlanner.Keys(LifecycleOf(decision).Timers, entity, row);

    /// <inheritdoc />
    public IReadOnlyList<TimerChange> ReplanTimers(PolicyDecision decision, EntityTimerReplan replan)
    {
        ArgumentNullException.ThrowIfNull(replan);
        if (LifecycleOf(decision).Timers is not { Count: > 0 } timers)
        {
            return [];
        }

        using var pass = budget.Enter();
        return EntityTimerPlanner.Replan(timers, replan, depth);
    }

    private static EntityLifecycle LifecycleOf(PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsDenied)
        {
            throw new InvalidOperationException("A denied decision reached the lifecycle; the write path must refuse it first.");
        }

        return decision.Lifecycle as EntityLifecycle
            ?? throw new InvalidOperationException("An allowed decision reached the lifecycle without the compiled lifecycle the policy engine resolves with it; the machine cannot be judged, so the write is refused.");
    }
}
