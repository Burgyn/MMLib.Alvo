using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules.Internal;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The dispatcher driven one round at a time through <see cref="IAlvoEventPump"/> — the mode the test host runs in
/// (spec 2026-10-14 §4.15). A round is one tick of the background loop, so these facts reuse that loop's fixtures.
/// </summary>
public sealed partial class OutboxDispatcherTests
{
    private const string DealsHook = "/entities/deals/hooks/afterUpdate/0";

    [Fact]
    public async Task A_manual_round_dispatches_what_is_due_and_reports_it()
    {
        var store = StoreWith(WonDeal(), WonDeal(), Event("entity.vehicles.created"));
        using var pump = Pump(Subject(store, Ready, Manual));

        var round = await pump.DrainAsync(Cancellation);

        round.Claimed.ShouldBe(3);
        round.Failed.ShouldBe(0);
        round.IsQuiet.ShouldBeFalse();
        round.Subscribers.ShouldBe([DealsHook], "each subscriber is named once, however many events it ran for");
        store.DispatchedIds.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_round_over_an_empty_queue_is_quiet()
    {
        using var pump = Pump(Subject(StoreWith(), Ready, Manual));

        var round = await pump.DrainAsync(Cancellation);

        round.IsQuiet.ShouldBeTrue();
        round.Claimed.ShouldBe(0);
        round.Subscribers.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_attempt_is_counted_and_released_for_a_later_round()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, Ready, Manual, HttpStatusCode.InternalServerError));

        var round = await pump.DrainAsync(Cancellation);

        round.Claimed.ShouldBe(1);
        round.Failed.ShouldBe(1);
        round.Subscribers.ShouldBe([DealsHook]);
        store.ReleasedIds.ShouldHaveSingleItem();
        store.DispatchedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_round_in_the_background_mode_is_refused_before_anything_is_claimed()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, Ready));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => pump.DrainAsync(Cancellation));

        refusal.Message.ShouldContain("Alvo:Events:DispatchMode");
        store.ClaimCount.ShouldBe(0, "two drivers of one outbox break per-key ordering, so the round must not start");
    }

    [Fact]
    public async Task A_round_with_events_switched_off_is_refused_before_anything_is_claimed()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, Ready, ManualButDisabled));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => pump.DrainAsync(Cancellation));

        refusal.Message.ShouldContain("Alvo:Events:Enabled");
        refusal.Message.ShouldNotContain("background loop", Case.Insensitive, "the mode is manual; nothing drains at all");
        store.ClaimCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_round_before_the_boot_is_ready_is_refused_without_claiming()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, null, Manual));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => pump.DrainAsync(Cancellation));

        refusal.Message.ShouldContain("Ready");
        store.ClaimCount.ShouldBe(0);
        store.EnsureCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_round_with_no_primed_catalog_is_refused_without_claiming()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, Ready, Manual, catalogs: new PolicyCatalogProvider()));

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => pump.DrainAsync(Cancellation));

        refusal.Message.ShouldContain("Apply a descriptor");
        refusal.Message.ShouldContain("Alvo__Schema__Project");
        refusal.Message.ShouldNotContain("boot finish", Case.Insensitive, "the boot did finish; it primed nothing");
        store.ClaimCount.ShouldBe(0, "an event judged against an unprimed catalog would be retired as delivered");
        store.DispatchedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_manual_dispatcher_never_pumps_on_its_own_and_says_so()
    {
        var store = StoreWith(WonDeal());
        await using var pump = await StartAsync(store, Ready, Manual);

        await Task.Delay(_manyPollIntervals, Cancellation);

        store.ClaimCount.ShouldBe(0, "in the manual mode only DrainAsync claims");
        pump.Service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        _logs.Entries.ShouldContain(
            entry => entry.Message.Contains("manual dispatch mode", StringComparison.Ordinal),
            "a loop that stands down must say why, or a misconfigured host delivers nothing silently");
    }

    [Fact]
    public async Task The_store_is_brought_up_once_across_rounds()
    {
        var store = StoreWith(WonDeal());
        using var pump = Pump(Subject(store, Ready, Manual));

        await pump.DrainAsync(Cancellation);
        await pump.DrainAsync(Cancellation);

        store.EnsureCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rounds_called_together_run_one_after_the_other()
    {
        var store = StoreWith(WonDeal());
        var blocked = new TaskCompletionSource();
        using var pump = Pump(Subject(store, Ready, Manual, blockDeliveriesOn: blocked));

        var first = pump.DrainAsync(Cancellation);
        await UntilAsync(() => store.ClaimCount == 1, "the first round to claim");
        var second = pump.DrainAsync(Cancellation);
        await Task.Delay(_manyPollIntervals, Cancellation);

        store.ClaimCount.ShouldBe(1, "a second round waits for the first instead of claiming beside it");
        blocked.SetResult();
        (await first).Claimed.ShouldBe(1);
        (await second).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task A_round_still_running_when_the_pump_is_disposed_keeps_its_own_outcome()
    {
        var store = StoreWith(WonDeal());
        var blocked = new TaskCompletionSource();
        var pump = Pump(Subject(store, Ready, Manual, blockDeliveriesOn: blocked));

        var round = pump.DrainAsync(Cancellation);
        await UntilAsync(() => store.ClaimCount == 1, "the round to claim");
        pump.Dispose();
        blocked.SetResult();

        (await round).Claimed.ShouldBe(1, "a host stopping under a round must not replace its outcome with a disposal error");
    }

    private static EventPump Pump(OutboxDispatcher dispatcher) => new(dispatcher);

    private static void Manual(AlvoEventOptions options) => options.DispatchMode = AlvoEventDispatchMode.Manual;

    private static void ManualButDisabled(AlvoEventOptions options)
    {
        Manual(options);
        options.Enabled = false;
    }
}
