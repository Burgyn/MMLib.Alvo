namespace MMLib.Alvo.Events;

/// <summary>
/// Runs one round of every event dispatcher Alvo runs, on the caller's schedule, when
/// <see cref="AlvoEventOptions.DispatchMode"/> is <see cref="AlvoEventDispatchMode.Manual"/>.
/// </summary>
/// <remarks>
/// <para>
/// A round is what the background dispatchers do on one tick each: claim the work that is due, run it, retire what
/// succeeded and hand back what failed under the usual backoff. Nothing is skipped or shortened, so a caller driving the
/// pump sees what production delivers. Work a round causes — an event a delivery writes — is due in the next round.
/// </para>
/// <para>
/// <b>Refused unless the dispatchers are manual.</b> Two drivers of one queue break per-entity-key ordering the way two
/// dispatching replicas do, so in the background mode a round throws instead of racing the loops. Rounds called
/// concurrently run one after the other; a round called from inside a round — by a subscriber that drains — is refused
/// with an <see cref="InvalidOperationException"/>, because it would wait for itself. The refusal finds the round by its
/// execution context, so code that suppresses execution-context flow before it drains escapes the check and waits.
/// </para>
/// </remarks>
public interface IAlvoEventPump
{
    /// <summary>Runs one dispatch round.</summary>
    /// <param name="cancellationToken">
    /// Cancels the round, as the host stopping does; work in flight is left claimed, and its lease returns it to the
    /// queue.
    /// </param>
    /// <returns>What the round claimed, which subscribers it attempted and how many claimed entries failed.</returns>
    /// <exception cref="InvalidOperationException">
    /// The dispatch mode is not <see cref="AlvoEventDispatchMode.Manual"/>, <see cref="AlvoEventOptions.Enabled"/> is
    /// off, or the boot has not primed a schema; nothing was claimed — or the call was made from inside a round.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, or the host is stopping — including a call made once it has begun
    /// to stop, which is refused before anything is claimed.
    /// </exception>
    Task<AlvoDrainResult> DrainAsync(CancellationToken cancellationToken = default);
}
