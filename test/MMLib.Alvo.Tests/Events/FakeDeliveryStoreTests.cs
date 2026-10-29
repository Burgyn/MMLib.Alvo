using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Holds the core's in-memory delivery store to the same contract the engines keep.</summary>
public sealed class FakeDeliveryStoreTests : DeliveryStoreContractTests
{
    protected override Task<IDeliveryStoreWorld> WorldAsync() => Task.FromResult<IDeliveryStoreWorld>(new World());

    /// <summary>
    /// A <em>released</em> outbox entry at the ceiling — no holder, <c>claimed_at</c> stamped forward to its backoff —
    /// holds no lane back, as in the relational claim, where only a held entry (<c>claimed_by</c> set) under a live claim
    /// blocks. The contract world cannot express a released entry, so the fake is pinned here.
    /// </summary>
    [Fact]
    public async Task A_released_outbox_entry_at_the_ceiling_holds_no_lane_back()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var store = new FakeDeliveryStore(clock);
        var released = EventOn(KeyA, clock);
        store.Publish(released, dispatched: false, attempts: OutboxCeiling, claimedAt: clock.GetUtcNow());
        store.ReleaseOutboxEntry(released.Id, clock.GetUtcNow() + TimeSpan.FromMinutes(10));
        var later = EventOn(KeyA, clock);
        store.Publish(later);
        await store.FanOutAsync([new DeliveryRequest { EventId = later.Id, Subscriber = "send-confirmation", PartitionKey = KeyA, Kind = "handler" }], TestContext.Current.CancellationToken);

        var claim = new DeliveryClaim { Claimant = "worker-1", Ordered = ["send-confirmation"], BatchSize = 10, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = OutboxCeiling, OutboxClaimLease = TimeSpan.FromMinutes(5) };

        (await store.ClaimAsync(claim, TestContext.Current.CancellationToken)).ShouldHaveSingleItem().EventId.ShouldBe(later.Id);
    }

    /// <summary>
    /// A log row carrying an idempotency key without the scope it belongs to is refused before anything changes, as the EF
    /// store refuses it: a key with no scope could never be found again, so a retry would run twice.
    /// </summary>
    [Theory]
    [InlineData("redeliver")]
    [InlineData("replay")]
    [InlineData("pause")]
    public async Task A_key_without_its_scope_is_refused(string operation)
    {
        var store = new FakeDeliveryStore(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));
        var entry = new DeliveryOperationEntry
        {
            Id = Guid.NewGuid(),
            At = DateTimeOffset.UnixEpoch,
            Actor = "ops",
            Operation = operation,
            Target = "send-confirmation",
            IdempotencyKey = "key-1",
        };

        await Should.ThrowAsync<ArgumentException>(() => Operate(store, operation, entry));

        store.Operations.ShouldBeEmpty();
    }

    /// <summary>
    /// A pause whose log row is refused — its id already recorded — throws and pauses nothing, as the EF store refuses it in
    /// the transaction that would have written the control.
    /// </summary>
    [Fact]
    public async Task A_pause_whose_log_row_is_refused_changes_nothing()
    {
        var store = new FakeDeliveryStore(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));
        var entry = new DeliveryOperationEntry { Id = Guid.NewGuid(), At = DateTimeOffset.UnixEpoch, Actor = "ops", Operation = "replay", Target = "send-confirmation" };
        await store.ReplayAsync(new DeliveryFilter { Subscriber = "send-confirmation", Limit = 20 }, entry, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => store.SetPausedAsync("send-confirmation", paused: true, entry with { Operation = "pause" }, TestContext.Current.CancellationToken));

        (await store.ControlsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        store.Operations.ShouldHaveSingleItem().Operation.ShouldBe("replay");
    }

    private static Task Operate(FakeDeliveryStore store, string operation, DeliveryOperationEntry entry) => operation switch
    {
        "redeliver" => store.RedeliverAsync(Guid.NewGuid(), entry, TestContext.Current.CancellationToken),
        "replay" => store.ReplayAsync(new DeliveryFilter { Limit = 1 }, entry, TestContext.Current.CancellationToken),
        _ => store.SetPausedAsync("send-confirmation", paused: true, entry, TestContext.Current.CancellationToken),
    };

    private const string KeyA = "tickets:a";
    private const int OutboxCeiling = 10;

    private static AlvoEvent EventOn(string partitionKey, TimeProvider clock) => new()
    {
        Id = AlvoEventId.Create(clock.GetUtcNow()),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.updated",
        Time = clock.GetUtcNow(),
        Subject = partitionKey.Replace(':', '/'),
        PartitionKey = partitionKey,
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = AlvoRecord.Empty },
    };

    private sealed class World : IDeliveryStoreWorld
    {
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        private readonly FakeDeliveryStore _store;

        internal World() => _store = new FakeDeliveryStore(_clock);

        public IDeliveryStore Store => _store;

        public Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync() => Task.FromResult(_store.Operations);

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
            var @event = EventOn(partitionKey, _clock);
            _store.Publish(@event, dispatched, attempts, claimed ? _clock.GetUtcNow() : null);

            return Task.FromResult(@event.Id);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
