using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Fan-out runs first (spec 2026-10-14 §4.8, Appendix A M1): a failing after-hook action no longer keeps a handler from
/// its delivery, and a failed fan-out holds back the later events of its record.
/// </summary>
public sealed partial class OutboxDispatcherTests
{
    private const string DealHandler = "deal-won";

    [Fact]
    public async Task Fan_out_runs_first_and_a_failing_after_hook_action_keeps_the_handler_s_delivery()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var store = StoreWith(WonDeal());
        var pump = Subject(store, Ready, status: HttpStatusCode.InternalServerError, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldBeEmpty("the webhook failed, so the entry is handed back");
        deliveries.Rows.ShouldHaveSingleItem().Subscriber.ShouldBe(DealHandler);
    }

    [Fact]
    public async Task A_fan_out_that_fails_holds_back_the_later_events_of_its_record()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System) { FanOutFailures = 1 };
        var first = WonDeal();
        var second = WonDeal();
        var store = StoreWith(first, second);
        var pump = Subject(store, Ready, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        deliveries.Rows.ShouldBeEmpty();
        store.ReleasedIds.ShouldBe([first.Id], "only the entry whose fan-out failed used an attempt");
        store.HeldBackIds.ShouldBe([second.Id]);
        store.EntryOf(second.Id).Attempts.ShouldBe(0, "a held-back entry was not tried, so its claim is not counted");
        store.HeldBackFor.ShouldBe([store.Backoffs[0]], "the later event waits exactly as long as the one it must follow");

        await pump.PumpOneBatchAsync(Cancellation);

        var claim = new DeliveryClaim
        {
            Claimant = "worker",
            Ordered = [DealHandler],
            BatchSize = 10,
            Lease = TimeSpan.FromMinutes(1),
            OutboxMaxAttempts = 10,
            OutboxClaimLease = TimeSpan.FromMinutes(5),
        };
        (await deliveries.ClaimAsync(claim, Cancellation)).ShouldHaveSingleItem().EventId
            .ShouldBe(first.Id, "event 2 of the same record is not delivered before event 1");
    }

    [Fact]
    public async Task An_event_that_fans_out_is_dispatched_rather_than_filtered()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var store = StoreWith(Event("entity.deals.updated", Record(("stage", "lead")), Record(("stage", "lead"))));
        var pump = Subject(store, Ready, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldHaveSingleItem();
        deliveries.Rows.ShouldHaveSingleItem();
    }

    /// <summary>A stopped key holds back only its own record: an entry of another key in the same batch is delivered.</summary>
    [Fact]
    public async Task Another_record_flows_while_one_is_held_back()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System) { FanOutFailures = 1 };
        var (first, second, other) = (WonDeal(), WonDeal(), OtherRecord());
        var store = StoreWith(first, second, other);

        await Subject(store, Ready, fanOut: FanOutTo(deliveries)).PumpOneBatchAsync(Cancellation);

        store.HeldBackIds.ShouldBe([second.Id]);
        store.DispatchedIds.ShouldBe([other.Id]);
        deliveries.Rows.ShouldHaveSingleItem().EventId.ShouldBe(other.Id);
    }

    /// <summary>A hand-back the store refuses is contained to its entry: the rest of the batch is still delivered.</summary>
    [Fact]
    public async Task A_hand_back_the_store_refuses_does_not_stop_the_batch()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System) { FanOutFailures = 1 };
        var (first, second, other) = (WonDeal(), WonDeal(), OtherRecord());
        var store = StoreWith(first, second, other);
        store.HoldBackThrows = true;

        await Subject(store, Ready, fanOut: FanOutTo(deliveries)).PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldBe([other.Id]);
        store.EntryOf(second.Id).Claimed.ShouldBeTrue("it stays claimed until its lease ends, so nothing overtakes it");
        _logs.Entries.ShouldContain(entry => entry.EventName == "HoldBackFailed" && entry.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The entry is retried because its webhook failed; the retry re-fans it out into the row that already exists, so the
    /// dead delivery is recorded and counted once.
    /// </summary>
    [Fact]
    public async Task A_retried_entry_leaves_one_dead_delivery_counted_once()
    {
        var handler = $"loop-{Guid.NewGuid():N}";
        using var dead = new DeadDeliveries(handler);
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var store = StoreWith(WonDeal() with { ChainDepth = AlvoChainDepthLimit.DefaultMaxDepth });
        var pump = Subject(store, Ready, status: HttpStatusCode.InternalServerError, fanOut: FanOutTo(deliveries, handler));

        await pump.PumpOneBatchAsync(Cancellation);
        await pump.PumpOneBatchAsync(Cancellation);

        store.ReleasedIds.Count.ShouldBe(2, "both attempts ran");
        deliveries.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
        dead.Count.ShouldBe(1);
    }

    /// <summary>Ruling 21: an event whose only reach is a dead delivery is dispatched, not filtered.</summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    public void An_event_is_counted_dispatched_when_it_reached_a_hook_or_any_delivery(int hooks, int deliveries, bool dispatched) =>
        OutboxDispatcher.Counted(hooks, deliveries).ShouldBeSameAs(dispatched ? AlvoEventMetrics.Dispatched : AlvoEventMetrics.Filtered);

    private static AlvoEvent OtherRecord() => WonDeal() with
    {
        Subject = "deals/019000aa-0000-7000-8000-0000000000ee",
        PartitionKey = "deals:019000aa-0000-7000-8000-0000000000ee",
    };

    private DeliveryFanOut FanOutTo(FakeDeliveryStore deliveries, string handler = DealHandler) => new(
        deliveries,
        new ExtensionBinder(
            HandlerCatalog.Registry(alvo => alvo.Entity("deals", d => d.OnUpdated(handler, (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask))),
            CelFixtures.Compiler,
            HandlerCatalog.EveryService(),
            _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());
}
