using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The fake delivery store, except that a concurrent replay under the same key commits just before this one's reset: the
/// racing run is recorded — with <paramref name="racingFingerprint"/>, or this run's own when <see langword="null"/> — and
/// this run's <see cref="IDeliveryStore.ReplayAsync"/> answers <see langword="null"/>, as the store's contract says it does
/// for a key already recorded.
/// </summary>
internal sealed class RacedReplayStore(IDeliveryStore inner, string? racingFingerprint = null) : IDeliveryStore
{
    public async Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        await inner.ReplayAsync(filter, entry with { Id = Guid.NewGuid(), Fingerprint = racingFingerprint ?? entry.Fingerprint }, cancellationToken);

        return await inner.ReplayAsync(filter, entry, cancellationToken);
    }

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
        inner.FanOutAsync(requests, cancellationToken);

    public Task EnsureAsync(CancellationToken cancellationToken = default) => inner.EnsureAsync(cancellationToken);

    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default) =>
        inner.ClaimAsync(claim, cancellationToken);

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
        inner.CompleteAsync(attempt, cancellationToken);

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
        inner.FailAsync(attempt, failure, cancellationToken);

    public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
        inner.PendingSubscribersAsync(cancellationToken);

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken);

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        inner.CancelPendingAsync(subscriber, cancellationToken);

    public Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
        inner.ListAsync(filter, cancellationToken);

    public Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);

    public Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.AttemptsAsync(id, cancellationToken);

    public Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
        inner.SelectAsync(filter, cancellationToken);

    public Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default) =>
        inner.RedeliverAsync(id, entry, cancellationToken);

    public Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default) =>
        inner.SetPausedAsync(subscriber, paused, entry, cancellationToken);

    public Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default) =>
        inner.ControlsAsync(cancellationToken);

    public Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default) =>
        inner.FindOperationAsync(scope, key, cancellationToken);
}
