using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The descriptor's <c>stateMachine</c> in the write: judged at stage 3 over the candidate the declarative hooks left, before
/// any host hook, and again at stage 5 beside <see cref="EnsureWriteAllowed"/> over what the host's patch left — and its
/// <c>timers</c>, armed, moved and cancelled beside the write's outbox event, in the write's own transaction.
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

    /// <summary>
    /// Writes the timer changes one write makes into the timer table, on the write's own connection and transaction, so the
    /// timers commit or roll back with the row and its event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One site</b>: every write path emits once per row with its unmasked images, and a replay never reaches the emit, so
    /// a replayed write arms nothing twice.
    /// </para>
    /// <para>
    /// <b>The upsert mints a fresh generation and clears the lease</b>, so a fire the timer pump claimed before this write
    /// moved the timer finds a different generation and retires nothing; the timer fires once, at its new time. Lock order:
    /// the write holds its row, then the timer row; a fire touches the timer row and the outbox only — no cycle.
    /// </para>
    /// </remarks>
    private async Task ArmTimersAsync(
        DbConnection connection, DbTransaction transaction, PolicyDecision decision, EntityTimerWrite write, CancellationToken cancellationToken)
    {
        if (!_lifecycle.ArmsTimers(decision))
        {
            return;
        }

        foreach (var change in _lifecycle.TimerChanges(decision, write))
        {
            _ = change.Arm is { } timer
                ? await TimerWrites.UpsertAsync(connection, transaction, _timerTable, timer, Guid.CreateVersion7(_time.GetUtcNow()), cancellationToken)
                : await TimerWrites.CancelAsync(connection, transaction, _timerTable, change.Scope, change.Key, cancellationToken);
        }
    }

    /// <summary>A batch row whose state move its entity's machine refuses.</summary>
    private static AlvoRowRefusal Transitioned(int index, AlvoConstraintViolationException refused) =>
        new(index, "transition", refused.Message, TransitionFix);
}
