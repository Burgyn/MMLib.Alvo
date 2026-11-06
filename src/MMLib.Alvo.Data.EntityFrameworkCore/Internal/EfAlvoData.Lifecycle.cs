using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The descriptor's <c>stateMachine</c> in the write: judged at stage 3 over the candidate the declarative hooks left, before
/// any host hook, and again at stage 5 beside <see cref="EnsureWriteAllowed"/> over what the host's patch left.
/// </summary>
internal sealed partial class EfAlvoData
{
    /// <summary>The fix a batch row refused by its entity's state machine carries; the Data API's single-row answer says the same.</summary>
    private const string TransitionFix =
        "Send a state a declared transition reaches from the current one — the entity's stateMachine lists them — or leave the field unchanged.";

    /// <summary>
    /// The entity's compiled <c>stateMachine</c> and <c>timers</c>, through an internal port no host can replace — the machine
    /// cannot be switched off by a registration.
    /// </summary>
    private readonly IEntityLifecycle _lifecycle;

    /// <summary>The timer table a write arms its entity's declarative timers in.</summary>
    private readonly string _timerTable;

    /// <summary>
    /// Refuses a write whose state move the entity's machine does not admit, judged against the very decision the write's rules
    /// came from.
    /// </summary>
    /// <remarks>
    /// <b>Twice per write, and the second run is not redundant.</b> Stage 3 runs before any host hook, so a host never sees a move
    /// the machine refuses. A host's before-write may not set the state field (<c>invalid-set</c>), but it may set a field a
    /// transition's <c>when</c> reads; stage 5 therefore judges the move again over what will actually be stored, as
    /// <see cref="EnsureWriteAllowed"/> judges the rules again.
    /// </remarks>
    private void EnsureTransition(
        PolicyDecision decision, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context) =>
        _lifecycle.EnsureTransition(decision, operation, candidate, previous, context);

    /// <summary><see cref="EnsureTransition"/> for one row of a batch: its refusal is the row's, collected rather than thrown.</summary>
    private AlvoRowRefusal? TransitionRefusal(
        PolicyDecision decision, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context, int index)
    {
        try
        {
            EnsureTransition(decision, operation, candidate, previous, context);
            return null;
        }
        catch (AlvoConstraintViolationException refused) when (refused.Kind == AlvoConstraintKind.Transition)
        {
            return Transitioned(index, refused);
        }
        catch (AlvoAuthorizationException forbidden)
        {
            return Forbidden(index, forbidden.Message);
        }
    }

    /// <summary>A batch row whose state move its entity's machine refuses.</summary>
    private static AlvoRowRefusal Transitioned(int index, AlvoConstraintViolationException refused) =>
        new(index, "transition", refused.Message, TransitionFix);
}
