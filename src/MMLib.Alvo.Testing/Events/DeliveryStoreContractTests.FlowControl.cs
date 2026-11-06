using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>Flow control: a debounced fan-out supersedes its lane's waiting deliveries.</summary>
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

    private static DeliveryRequest Debounced(Guid @event, string key, string tenant) =>
        Request(@event, Confirm, key) with { TenantId = tenant, Debounce = _debounce };

    private static async Task<StoredDelivery> DeliveryOfAsync(IDeliveryStoreWorld world, Guid @event) =>
        (await world.Store.ListAsync(new DeliveryFilter { Limit = 100 }, Ct)).Single(delivery => delivery.EventId == @event);
}
