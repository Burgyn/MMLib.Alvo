using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The provider's delivery store, except that the first fan-out throws, as a database that dropped one insert would.</summary>
internal sealed class FailingFirstFanOut(IDeliveryStore inner) : IDeliveryStore
{
    private int _failed;

    internal static void Install(IServiceCollection services)
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IDeliveryStore));
        services.AddSingleton<IDeliveryStore>(provider => new FailingFirstFanOut((IDeliveryStore)original.ImplementationFactory!(provider)));
    }

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _failed, 1) == 0
            ? Task.FromException<int>(new InvalidOperationException("injected: the first fan-out fails"))
            : inner.FanOutAsync(requests, cancellationToken);

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

    public Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default) =>
        inner.ReplayAsync(filter, entry, cancellationToken);

    public Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default) =>
        inner.FindOperationAsync(scope, key, cancellationToken);
}
