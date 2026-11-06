using MMLib.Alvo.Data;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The descriptor's <c>stateMachine</c> and <c>timers</c> for the data path: the transition guard a write passes before any
/// host hook, and the timer changes it writes beside its outbox event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, so a host cannot replace it</b> — the machine cannot be switched off by a registration (fail closed). It is
/// the <see cref="IBeforeHookRunner"/> precedent in every other respect: synchronous and token-free, because it runs while
/// the write holds its transaction, and no network belongs there.
/// </para>
/// <para>
/// <b>Every member takes the write's own <see cref="PolicyDecision"/></b>, and reads the compiled lifecycle that decision was
/// resolved with — never the catalog's current revision, which an apply may have replaced between the decision and the
/// guard. The machine is therefore judged against the very schema revision the rules judging the same write came from.
/// </para>
/// </remarks>
internal interface IEntityLifecycle
{
    /// <summary>Refuses a write whose state move its entity's state machine does not admit.</summary>
    /// <param name="decision">The write's allowed decision.</param>
    /// <param name="operation">The write's operation.</param>
    /// <param name="candidate">The complete row the write would store.</param>
    /// <param name="previous">The stored row, or <see langword="null"/> on a create.</param>
    /// <param name="caller">The caller, whose roles a transition may require.</param>
    /// <exception cref="AlvoConstraintViolationException">The move matches no admitting transition (<see cref="AlvoConstraintKind.Transition"/>).</exception>
    /// <exception cref="AlvoAuthorizationException">Every transition the move matches needs a role the caller lacks.</exception>
    void EnsureTransition(PolicyDecision decision, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext caller);

    /// <summary>Whether the decision's entity declares any timer, so the write has timer changes to plan.</summary>
    /// <param name="decision">The write's allowed decision.</param>
    bool ArmsTimers(PolicyDecision decision);

    /// <summary>The timers one write arms, moves and cancels.</summary>
    /// <param name="decision">The write's allowed decision.</param>
    /// <param name="write">The write.</param>
    IReadOnlyList<TimerChange> TimerChanges(PolicyDecision decision, EntityTimerWrite write);
}

/// <summary>
/// The compiled lifecycle a <see cref="PolicyDecision"/> was resolved with. Opaque outside the core, which alone compiles
/// and reads it.
/// </summary>
internal interface IDecidedLifecycle;
