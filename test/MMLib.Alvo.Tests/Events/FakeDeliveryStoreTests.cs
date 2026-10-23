using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Holds the core's in-memory delivery store to the same contract the engines keep.</summary>
public sealed class FakeDeliveryStoreTests : DeliveryStoreContractTests
{
    protected override Task<IDeliveryStoreWorld> WorldAsync() => Task.FromResult<IDeliveryStoreWorld>(new World());

    private sealed class World : IDeliveryStoreWorld
    {
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        private readonly FakeDeliveryStore _store;

        internal World() => _store = new FakeDeliveryStore(_clock);

        public IDeliveryStore Store => _store;

        public void Advance(TimeSpan duration) => _clock.Advance(duration);

        public Task<Guid> AppendEventAsync(string partitionKey) => AppendAsync(partitionKey, dispatched: true, attempts: 0, claimed: false);

        public Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0, bool claimed = false) =>
            AppendAsync(partitionKey, dispatched: false, attempts, claimed);

        public Task MarkDispatchedAsync(Guid eventId)
        {
            _store.MarkDispatched(eventId);
            return Task.CompletedTask;
        }

        private Task<Guid> AppendAsync(string partitionKey, bool dispatched, int attempts, bool claimed)
        {
            var @event = new AlvoEvent
            {
                Id = AlvoEventId.Create(_clock.GetUtcNow()),
                Source = AlvoEvent.DefaultSource,
                Type = "entity.tickets.updated",
                Time = _clock.GetUtcNow(),
                Subject = partitionKey.Replace(':', '/'),
                PartitionKey = partitionKey,
                AuthType = AlvoEventAuthType.ApiKey,
                CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
                Data = new AlvoEventData { Record = AlvoRecord.Empty },
            };
            _store.Publish(@event, dispatched, attempts, claimed ? _clock.GetUtcNow() : null);

            return Task.FromResult(@event.Id);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
