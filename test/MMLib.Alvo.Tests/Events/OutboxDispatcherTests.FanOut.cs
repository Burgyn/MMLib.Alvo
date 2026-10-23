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

    private DeliveryFanOut FanOutTo(FakeDeliveryStore deliveries) => new(
        deliveries,
        new ExtensionBinder(
            HandlerCatalog.Registry(alvo => alvo.Entity("deals", d => d.OnUpdated(DealHandler, (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask))),
            CelFixtures.Compiler,
            HandlerCatalog.EveryService(),
            _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());
}
