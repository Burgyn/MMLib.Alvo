using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// Flow control: a debounced fan-out supersedes its lane's waiting deliveries, and a capped subscriber runs no more of one
/// key's deliveries at once than its cap.
/// </summary>
public abstract partial class DeliveryStoreContractTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static readonly TimeSpan _debounce = TimeSpan.FromSeconds(5);

    /// <summary>A debounced delivery is pending, due its debounce after the fan-out.</summary>
    [Fact]
    public async Task A_debounced_fan_out_is_due_after_its_debounce()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Debounced(@event, KeyA, TenantA)], Ct);

        (await DeliveryOfAsync(world, @event)).Status.ShouldBe("pending");
        world.Advance(_debounce - TimeSpan.FromSeconds(1));
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("it waits out its debounce");
        world.Advance(TimeSpan.FromSeconds(1));
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(@event);
    }

    /// <summary>A newer debounced delivery of a lane supersedes the older one still waiting, which ends at once.</summary>
    [Fact]
    public async Task A_debounced_fan_out_supersedes_the_lanes_waiting_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var older = await world.AppendEventAsync(KeyA);
        var newer = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Debounced(older, KeyA, TenantA)], Ct);
        await world.Store.FanOutAsync([Debounced(newer, KeyA, TenantA)], Ct);

        var superseded = await DeliveryOfAsync(world, older);
        var waiting = await DeliveryOfAsync(world, newer);
        (superseded.Status, superseded.CompletedAt).ShouldBe(("superseded", (DateTimeOffset?)(waiting.NotBefore - _debounce)));
        waiting.Status.ShouldBe("pending");
        world.Advance(_debounce);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(newer);
    }

    /// <summary>A re-fan-out of an older event, after a newer one of its lane, is superseded by its own fan-out.</summary>
    [Fact]
    public async Task A_late_fan_out_of_an_older_event_is_superseded_at_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var older = await world.AppendEventAsync(KeyA);
        var newer = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Debounced(newer, KeyA, TenantA)], Ct);
        await world.Store.FanOutAsync([Debounced(older, KeyA, TenantA)], Ct);

        (await DeliveryOfAsync(world, older)).Status.ShouldBe("superseded");
        (await DeliveryOfAsync(world, newer)).Status.ShouldBe("pending");
    }

    /// <summary>A running delivery is never superseded: it finishes, and the newer one waits behind it.</summary>
    [Fact]
    public async Task A_debounced_fan_out_never_supersedes_a_running_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var older = await world.AppendEventAsync(KeyA);
        var newer = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Debounced(older, KeyA, TenantA)], Ct);
        world.Advance(_debounce);
        var running = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        await world.Store.FanOutAsync([Debounced(newer, KeyA, TenantA)], Ct);

        (await DeliveryOfAsync(world, older)).Status.ShouldBe("running");
        (await world.Store.CompleteAsync(Attempt(running), Ct)).ShouldBeTrue();
    }

    /// <summary>A sleeping delivery started its work, so a newer debounced one does not supersede it.</summary>
    [Fact]
    public async Task A_debounced_fan_out_never_supersedes_a_sleeping_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var older = await world.AppendEventAsync(KeyA);
        var newer = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Debounced(older, KeyA, TenantA)], Ct);
        world.Advance(_debounce);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.SleepAsync(Attempt(claimed), (await DeliveryOfAsync(world, older)).NotBefore + _sleep, Ct);

        await world.Store.FanOutAsync([Debounced(newer, KeyA, TenantA)], Ct);

        (await DeliveryOfAsync(world, older)).Status.ShouldBe("pending");
    }

    /// <summary>One partition key in two tenants is two lanes: a debounced fan-out in one supersedes nothing in the other.</summary>
    [Fact]
    public async Task A_debounced_fan_out_supersedes_only_its_own_tenants_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var inA = await world.AppendEventAsync(KeyA);
        var inB = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Debounced(inA, KeyA, TenantA)], Ct);
        await world.Store.FanOutAsync([Debounced(inB, KeyA, TenantB)], Ct);

        (await DeliveryOfAsync(world, inA)).Status.ShouldBe("pending");
        (await DeliveryOfAsync(world, inB)).Status.ShouldBe("pending");
    }

    /// <summary>A superseded delivery holds no lane back and can be neither redelivered nor replayed.</summary>
    [Fact]
    public async Task A_superseded_delivery_holds_no_lane_and_is_not_replayable()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var older = await world.AppendEventAsync(KeyA);
        var newer = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Debounced(older, KeyA, TenantA)], Ct);
        await world.Store.FanOutAsync([Debounced(newer, KeyA, TenantA)], Ct);
        var superseded = await DeliveryOfAsync(world, older);
        world.Advance(_debounce);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(newer);
        (await world.Store.RedeliverAsync(superseded.Id, Entry("redeliver", superseded.Id.ToString()), Ct)).ShouldBeFalse();
        (await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Status = "superseded", Limit = 20 }, Entry("replay", Confirm), Ct))
            .ShouldNotBeNull().Affected.ShouldBe(0);
        (await DeliveryOfAsync(world, older)).Status.ShouldBe("superseded");
    }

    /// <summary>A claim takes no more of one tenant's deliveries than the cap, and another claimant takes none beside them.</summary>
    [Fact]
    public async Task A_claim_takes_no_more_deliveries_of_one_tenant_than_its_limit()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 5);

        (await world.Store.ClaimAsync(Capped("tenant", 2), Ct)).Count.ShouldBe(2);
        (await world.Store.ClaimAsync(Capped("tenant", 2) with { Claimant = OtherClaimant }, Ct)).ShouldBeEmpty();
    }

    /// <summary>A completed delivery gives its place back, so the next claim takes the next delivery of its key.</summary>
    [Fact]
    public async Task A_completed_delivery_frees_its_slot_for_the_next_claim()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 2);
        var first = (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem();

        (await world.Store.CompleteAsync(Attempt(first), Ct)).ShouldBeTrue();

        (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem().Id.ShouldNotBe(first.Id);
    }

    /// <summary>A failed attempt and a sleep give the delivery's place back too.</summary>
    [Fact]
    public async Task A_failed_or_sleeping_delivery_frees_its_slot()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 3);
        var failing = (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(failing), Failure("boom", TimeSpan.FromHours(1)), Ct);
        var sleeping = (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem();

        await world.Store.SleepAsync(Attempt(sleeping), (await DeliveryOfAsync(world, sleeping.EventId)).NotBefore + _sleep, Ct);

        (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem().Id.ShouldNotBeOneOf(failing.Id, sleeping.Id);
    }

    /// <summary>A place held by a delivery whose lease expired is taken again.</summary>
    [Fact]
    public async Task A_slot_whose_lease_expired_is_taken_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 2);
        (await world.Store.ClaimAsync(Capped("tenant", 1) with { Claimant = OtherClaimant }, Ct)).ShouldHaveSingleItem();

        world.Advance(_lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem();
    }

    /// <summary>
    /// A late finisher whose lease was taken over answers nothing, and gives back no place: the delivery's new claimant still
    /// holds it, and the key stays at its cap until that claimant answers.
    /// </summary>
    [Fact]
    public async Task A_late_finisher_releases_no_slot_it_no_longer_holds()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 2);
        var late = (await world.Store.ClaimAsync(Capped("tenant", 1) with { Claimant = OtherClaimant }, Ct)).ShouldHaveSingleItem();
        world.Advance(_lease + TimeSpan.FromSeconds(1));
        var current = (await world.Store.ClaimAsync(Capped("tenant", 1), Ct)).ShouldHaveSingleItem();

        (await world.Store.CompleteAsync(Attempt(late) with { Claimant = OtherClaimant }, Ct)).ShouldBeFalse();

        (await world.Store.ClaimAsync(Capped("tenant", 1) with { Claimant = "worker-3" }, Ct)).ShouldBeEmpty("the current claimant still holds the place");
        (await world.Store.CompleteAsync(Attempt(current), Ct)).ShouldBeTrue();
        (await world.Store.ClaimAsync(Capped("tenant", 1) with { Claimant = "worker-3" }, Ct)).ShouldHaveSingleItem();
    }

    /// <summary>The record key caps each record of each tenant on its own: one record's cap holds no other record back.</summary>
    [Fact]
    public async Task The_record_key_limits_each_record_of_each_tenant_on_its_own()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        foreach (var (key, tenant) in new[] { (KeyA, TenantA), (KeyA, TenantA), (KeyB, TenantA), (KeyA, TenantB) })
        {
            await world.Store.FanOutAsync([Request(await world.AppendEventAsync(key), Confirm, key) with { TenantId = tenant }], Ct);
        }

        var claimed = await world.Store.ClaimAsync(Capped("record", 1) with { Ordered = [], Unordered = [Confirm] }, Ct);

        claimed.Count.ShouldBe(3, "one of each (tenant, record): a-in-A, b-in-A, a-in-B");
        (await world.Store.ClaimAsync(Capped("record", 1) with { Ordered = [], Unordered = [Confirm], Claimant = OtherClaimant }, Ct)).ShouldBeEmpty();
    }

    /// <summary>A tenant at its cap with many deliveries waiting does not keep another tenant's delivery from the same claim.</summary>
    [Fact]
    public async Task A_saturated_key_does_not_starve_another_keys_deliveries()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await FanOutInTenantAsync(world, TenantA, 10);
        await FanOutInTenantAsync(world, TenantB, 1);

        var claimed = await world.Store.ClaimAsync(Capped("tenant", 1) with { BatchSize = 2 }, Ct);

        var tenants = new List<string?>();
        foreach (var delivery in claimed)
        {
            tenants.Add((await DeliveryOfAsync(world, delivery.EventId)).TenantId);
        }

        tenants.ShouldBe([TenantA, TenantB], ignoreOrder: true);
    }

    /// <summary>One claim takes a capped subscriber's deliveries under its cap and an uncapped one's as usual.</summary>
    [Fact]
    public async Task A_claim_takes_capped_and_uncapped_subscribers_together()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        for (var index = 0; index < 3; index++)
        {
            var key = $"tickets:{index}";
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key) with { TenantId = TenantA }, Request(@event, Audit, key) with { TenantId = TenantA }], Ct);
        }

        var claimed = await world.Store.ClaimAsync(Capped("tenant", 1) with { Ordered = [Confirm, Audit] }, Ct);

        claimed.Count(delivery => delivery.Subscriber == Confirm).ShouldBe(1);
        claimed.Count(delivery => delivery.Subscriber == Audit).ShouldBe(3);
    }

    /// <summary>
    /// An uncapped backlog older than a capped delivery, and big enough to fill the batch, takes no more than its share: the
    /// claim still takes the capped delivery, and the uncapped subscriber still gets the rest.
    /// </summary>
    [Fact]
    public async Task A_capped_subscriber_is_claimed_beside_an_uncapped_backlog_that_fills_the_batch()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        for (var index = 0; index < 5; index++)
        {
            var key = $"audit:{index}";
            await world.Store.FanOutAsync([Request(await world.AppendEventAsync(key), Audit, key) with { TenantId = TenantA }], Ct);
        }

        await world.Store.FanOutAsync([Request(await world.AppendEventAsync("tickets:1"), Confirm, "tickets:1") with { TenantId = TenantA }], Ct);

        var claimed = await world.Store.ClaimAsync(Capped("tenant", 1) with { Ordered = [Confirm, Audit], BatchSize = 2 }, Ct);

        claimed.Select(delivery => delivery.Subscriber).Order(StringComparer.Ordinal).ShouldBe([Audit, Confirm]);
    }

    /// <summary>
    /// A batch of one cannot hold both shares, so neither a capped nor an uncapped backlog may take every round: within two
    /// claims each subscriber gets one.
    /// </summary>
    [Fact]
    public async Task At_a_batch_of_one_capped_and_uncapped_backlogs_each_get_a_claim_within_two_rounds()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        for (var index = 0; index < 3; index++)
        {
            var key = $"tickets:{index}";
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key) with { TenantId = TenantA }, Request(@event, Audit, key) with { TenantId = TenantA }], Ct);
        }

        var claim = Capped("tenant", 3) with { Ordered = [Confirm, Audit], BatchSize = 1 };
        var first = await world.Store.ClaimAsync(claim, Ct);
        var second = await world.Store.ClaimAsync(claim, Ct);

        first.Concat(second).Select(delivery => delivery.Subscriber).Order(StringComparer.Ordinal).ShouldBe([Audit, Confirm]);
    }

    private static DeliveryRequest Debounced(Guid @event, string key, string tenant) =>
        Request(@event, Confirm, key) with { TenantId = tenant, Debounce = _debounce };

    private static DeliveryClaim Capped(string key, int limit) =>
        Claim(Confirm) with { Limits = [new DeliveryLimit { Subscriber = Confirm, Key = key, Limit = limit }] };

    private static async Task FanOutInTenantAsync(IDeliveryStoreWorld world, string tenant, int deliveries)
    {
        for (var index = 0; index < deliveries; index++)
        {
            var key = $"tickets:{tenant}:{index}";
            await world.Store.FanOutAsync([Request(await world.AppendEventAsync(key), Confirm, key) with { TenantId = tenant }], Ct);
        }
    }

    private static async Task<StoredDelivery> DeliveryOfAsync(IDeliveryStoreWorld world, Guid @event) =>
        (await world.Store.ListAsync(new DeliveryFilter { Limit = 100 }, Ct)).Single(delivery => delivery.EventId == @event);
}
