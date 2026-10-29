using MMLib.Alvo.Events;

namespace MMLib.Alvo.Testing.Events;

/// <summary>An <see cref="IDeliveryStore"/> over live storage, plus what its own surface cannot do: append an outbox entry, move time.</summary>
public interface IDeliveryStoreWorld : IAsyncDisposable
{
    /// <summary>Gets the store under test, over storage that already exists.</summary>
    IDeliveryStore Store { get; }

    /// <summary>Moves the store's clock forward by <paramref name="duration"/>.</summary>
    /// <param name="duration">How far time moves.</param>
    void Advance(TimeSpan duration);

    /// <summary>
    /// Appends one outbox entry under <paramref name="partitionKey"/>, already dispatched — the state an event is in once
    /// it was fanned out — and returns its id; a later call returns a later id.
    /// </summary>
    /// <param name="partitionKey">The entry's partition key.</param>
    /// <returns>The event id.</returns>
    Task<Guid> AppendEventAsync(string partitionKey);

    /// <summary>
    /// Appends one outbox entry that has not been fanned out yet — undispatched, with <paramref name="attempts"/> claims
    /// behind it — and returns its id.
    /// </summary>
    /// <param name="partitionKey">The entry's partition key.</param>
    /// <param name="attempts">How many times the outbox already claimed it.</param>
    /// <param name="claimed">Whether a claimant holds it now, claimed at the world's current instant.</param>
    /// <returns>The event id.</returns>
    Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0, bool claimed = false);

    /// <summary>Marks an outbox entry dispatched, as the outbox does once it fanned the entry out.</summary>
    /// <param name="eventId">The entry's id.</param>
    /// <returns>A task that completes when the entry is marked.</returns>
    Task MarkDispatchedAsync(Guid eventId);

    /// <summary>Every row of the operations log, by when it was written and then by id.</summary>
    /// <returns>The rows, as the store would find them.</returns>
    Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync();
}
