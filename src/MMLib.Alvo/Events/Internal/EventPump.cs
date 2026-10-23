namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The one <see cref="IAlvoEventPump"/>: a manual round of every event dispatcher this build runs, one round at a time.
/// </summary>
/// <remarks>
/// <para>
/// In this build the outbox dispatcher is the only one. HX-03 adds the delivery dispatcher as a second step of the same
/// round and HX-13 the timer and schedule pumps (spec 2026-10-14 §4.15: outbox → fan-out → deliveries → due timers);
/// the public port and its result do not change, and the results of the steps are summed.
/// </para>
/// <para>
/// The gate serialises rounds and is not re-entrant: a round called from inside a round waits for itself, which is why
/// the port says a subscriber must never drain.
/// </para>
/// </remarks>
/// <param name="outbox">The outbox dispatcher, whose manual round checks the mode and the boot before it claims.</param>
internal sealed class EventPump(OutboxDispatcher outbox) : IAlvoEventPump, IDisposable
{
    private readonly SemaphoreSlim _rounds = new(1, 1);

    /// <summary>Gets the outbox dispatcher this pump drives — the same instance the host runs.</summary>
    internal OutboxDispatcher Outbox => outbox;

    /// <inheritdoc/>
    public async Task<AlvoDrainResult> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _rounds.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await outbox.ManualRoundAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndRound();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _rounds.Dispose();

    /// <summary>Opens the gate for the next round, unless the container has already disposed it.</summary>
    /// <remarks>
    /// A host stopped while a round is still running disposes this pump under it. Letting
    /// <see cref="SemaphoreSlim.Release()"/> throw <see cref="ObjectDisposedException"/> from the <c>finally</c> would
    /// replace the round's own outcome — its result, or the failure that actually ended it — with a disposal error
    /// that says nothing; with the pump gone there is no next round to open the gate for.
    /// </remarks>
    private void EndRound()
    {
        try
        {
            _rounds.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
