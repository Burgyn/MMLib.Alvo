using Microsoft.Extensions.Hosting;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The one <see cref="IAlvoEventPump"/>: a manual round of every event dispatcher this build runs, one round at a time.
/// </summary>
/// <remarks>
/// <para>
/// A round is the outbox dispatcher's tick (claim, fan out, run the declarative actions) followed by the delivery
/// dispatcher's (claim the lane heads now due, run them); HX-13 adds the timer and schedule steps. The public port and its
/// result do not change; the steps' results are summed.
/// </para>
/// <para>
/// The gate serialises rounds. A round called from inside a round — a subscriber that drains — is <b>refused</b> rather than
/// left to wait for itself: the round marks its execution flow, and a nested call finds the mark. Code that suppresses
/// execution-context flow escapes the mark and would wait; nothing can see it.
/// </para>
/// </remarks>
/// <param name="outbox">The outbox dispatcher, whose manual round checks the mode and the boot before it claims.</param>
/// <param name="deliveries">The delivery dispatcher, whose manual round runs the handler deliveries now due.</param>
/// <param name="lifetime">The host's lifetime, whose stopping token ends a round the way it ends the loop's.</param>
internal sealed class EventPump(OutboxDispatcher outbox, DeliveryDispatcher deliveries, IHostApplicationLifetime lifetime)
    : IAlvoEventPump, IDisposable
{
    private readonly SemaphoreSlim _rounds = new(1, 1);
    private readonly AsyncLocal<bool> _inRound = new();

    /// <summary>Gets the outbox dispatcher this pump drives — the same instance the host runs.</summary>
    internal OutboxDispatcher Outbox => outbox;

    /// <summary>Gets the delivery dispatcher this pump drives — the same instance the host runs.</summary>
    internal DeliveryDispatcher Deliveries => deliveries;

    /// <inheritdoc/>
    /// <remarks>
    /// The round runs under the caller's token linked with <see cref="IHostApplicationLifetime.ApplicationStopping"/>,
    /// so a host stopping under a round the caller did not cancel — a test host disposed mid-drain, or a caller that
    /// passed <see cref="CancellationToken.None"/> — is a shutdown, as it is for the background loop: no failed span,
    /// no failed attempt, and the work in flight left claimed.
    /// </remarks>
    public async Task<AlvoDrainResult> DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_inRound.Value)
        {
            throw ReEntered();
        }

        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        await _rounds.WaitAsync(round.Token).ConfigureAwait(false);
        try
        {
            _inRound.Value = true;
            var fannedOut = await outbox.ManualRoundAsync(round.Token).ConfigureAwait(false);
            var delivered = await deliveries.ManualRoundAsync(round.Token).ConfigureAwait(false);

            return Sum(fannedOut, delivered);
        }
        finally
        {
            _inRound.Value = false;
            EndRound();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _rounds.Dispose();

    private static AlvoDrainResult Sum(AlvoDrainResult first, AlvoDrainResult second) => new()
    {
        Claimed = first.Claimed + second.Claimed,
        Failed = first.Failed + second.Failed,
        Subscribers = [.. first.Subscribers.Concat(second.Subscribers).Distinct(StringComparer.Ordinal)],
    };

    private static InvalidOperationException ReEntered() => new(
        "A dispatch round was refused: it was called from inside a round — a subscriber (an event handler, or a webhook "
        + "receiver running in this process) drained the pump. A round waits for the round it is in, so this would never "
        + "return. Return from the subscriber and drain from the code that wrote the event.");

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
