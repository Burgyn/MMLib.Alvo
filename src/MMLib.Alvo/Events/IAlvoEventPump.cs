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

    /// <summary>
    /// Runs the handler a fixture names, once, over the fixture's envelope — in its own scope, as its configured identity,
    /// one hop deeper in the chain, under its timeout — exactly as a delivery attempt would, and records nothing.
    /// </summary>
    /// <remarks>
    /// The way to reproduce a delivery taken from another instance: the handler's own exception reaches the caller as it was
    /// thrown. Refused unless the dispatchers are manual — a fixture is an event nobody wrote, and only a test host may run one.
    /// A typed handler reads the record as its host type first, so a redacted value in a typed field fails with a
    /// <see cref="InvalidOperationException"/> naming the field before the handler runs, and nothing is recorded: fill it in
    /// and run again.
    /// </remarks>
    /// <param name="fixture">The fixture, with whatever redacted values the reproduction needs filled in.</param>
    /// <param name="cancellationToken">Cancels the run, as the host stopping does.</param>
    /// <returns>A task that completes when the handler returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The dispatch mode is not <see cref="AlvoEventDispatchMode.Manual"/>, the boot is not ready, the handler is not bound,
    /// the envelope cannot be read, or the call was made from inside a round.
    /// </exception>
    /// <exception cref="TimeoutException">The handler ran past its timeout.</exception>
    Task DeliverAsync(Management.DeliveryFixture fixture, CancellationToken cancellationToken = default);
}
