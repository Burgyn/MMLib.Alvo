using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>The steps half of the delivery protocol: memoized step results, kept per (event, subscriber, step).</summary>
public abstract partial class DeliveryStoreContractTests
{
    private static readonly TimeSpan _sleep = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset _stepCompletedAt = new(2026, 11, 6, 9, 30, 15, 123, 456, TimeSpan.Zero);

    /// <summary>A recorded step is read back with its result and completion instant.</summary>
    [Fact]
    public async Task A_recorded_step_is_read_back_with_its_result()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        var recorded = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "\"deposit-1\""), Ct);

        recorded.ShouldBe(Step(@event, Confirm, "charge", "\"deposit-1\""));
        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().ShouldBe(recorded);
    }

    /// <summary>The first record of a step wins: a second record of the same step keeps, and returns, the first result.</summary>
    [Fact]
    public async Task A_second_record_of_one_step_keeps_the_first_result()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        var first = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "1"), Ct);
        var second = await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "2"), Ct);

        (first.Result, second.Result).ShouldBe(("1", "1"));
        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().Result.ShouldBe("1");
    }

    /// <summary>Two subscribers of one event keep their own step of the same name.</summary>
    [Fact]
    public async Task Steps_are_kept_per_subscriber_of_one_event()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "\"confirm\""), Ct);
        await world.Store.RecordStepAsync(Step(@event, Audit, "charge", "\"audit\""), Ct);

        (await world.Store.StepsAsync(@event, Confirm, Ct)).ShouldHaveSingleItem().Result.ShouldBe("\"confirm\"");
        (await world.Store.StepsAsync(@event, Audit, Ct)).ShouldHaveSingleItem().Result.ShouldBe("\"audit\"");
    }

    /// <summary>A read of one event's steps returns none of another event's.</summary>
    [Fact]
    public async Task Steps_of_another_event_are_not_read()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);

        await world.Store.RecordStepAsync(Step(first, Confirm, "charge", "null"), Ct);
        await world.Store.RecordStepAsync(Step(first, Confirm, "email", "null"), Ct);

        (await world.Store.StepsAsync(first, Confirm, Ct)).Select(step => step.Name).ShouldBe(["charge", "email"], ignoreOrder: true);
        (await world.Store.StepsAsync(second, Confirm, Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// A sleep puts the delivery back to pending, due at its wake time, records the attempt as skipped and spends no attempt:
    /// after five sleeps a failure still has every retry left.
    /// </summary>
    [Fact]
    public async Task A_sleep_returns_the_delivery_due_at_its_wake_time_without_spending_an_attempt()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var now = await FannedOutAtAsync(world);

        for (var sleep = 0; sleep < 5; sleep++)
        {
            var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
            claimed.Attempts.ShouldBe(1);
            now += _sleep;
            (await world.Store.SleepAsync(Attempt(claimed), now, Ct)).ShouldBeTrue();
            (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("asleep until its wake time");
            world.Advance(_sleep);
        }

        var woken = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(woken), Failure("boom", _retry), Ct);
        var stored = (await world.Store.FindAsync(woken.Id, Ct)).ShouldNotBeNull();
        (stored.Status, stored.Attempts).ShouldBe(("retrying", 1));
        (await world.Store.AttemptsAsync(woken.Id, Ct)).Select(attempt => attempt.Outcome)
            .ShouldBe(["skipped", "skipped", "skipped", "skipped", "skipped", "failed"]);
    }

    /// <summary>A sleeping delivery reads as pending, due at its wake time, with no attempt counted.</summary>
    [Fact]
    public async Task A_sleeping_delivery_reads_as_pending_at_its_wake_time()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        var wakeAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await world.Store.SleepAsync(Attempt(claimed), wakeAt, Ct);

        var stored = (await world.Store.FindAsync(claimed.Id, Ct)).ShouldNotBeNull();
        (stored.Status, stored.Attempts, stored.NotBefore, stored.LastErrorType).ShouldBe(("pending", 0, wakeAt, (string?)null));
    }

    /// <summary>A sleep answered by a claimant whose lease was taken over changes nothing and records nothing.</summary>
    [Fact]
    public async Task A_sleep_by_a_claimant_that_lost_its_lease_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var late = (await world.Store.ClaimAsync(Claim(Confirm) with { Claimant = OtherClaimant }, Ct)).ShouldHaveSingleItem();
        world.Advance(_lease + TimeSpan.FromSeconds(1));
        var current = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.SleepAsync(Attempt(late) with { Claimant = OtherClaimant }, DateTimeOffset.UnixEpoch, Ct)).ShouldBeFalse();

        (await world.Store.FindAsync(current.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("running");
        (await world.Store.AttemptsAsync(current.Id, Ct)).ShouldBeEmpty();
        (await world.Store.CompleteAsync(Attempt(current), Ct)).ShouldBeTrue();
    }

    /// <summary>A sleeping delivery of an ordered subscriber is pending, so it holds its lane until it wakes and finishes.</summary>
    [Fact]
    public async Task A_sleeping_ordered_delivery_holds_its_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyA)], Ct);
        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        await world.Store.SleepAsync(Attempt(head), await FannedOutAtAsync(world) + _sleep, Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the sleeping head holds the lane");
        world.Advance(_sleep);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(first);
    }

    /// <summary>A redeliver of a succeeded delivery clears its steps; a redeliver of a dead one keeps them, to resume after them.</summary>
    [Fact]
    public async Task A_redeliver_clears_the_steps_of_a_succeeded_delivery_and_keeps_those_of_a_dead_one()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var succeeded = await FinishedWithStepAsync(world, KeyA, dead: false);
        var dead = await FinishedWithStepAsync(world, KeyB, dead: true);

        (await world.Store.RedeliverAsync(succeeded.Id, Entry("redeliver", succeeded.Id.ToString()), Ct)).ShouldBeTrue();
        (await world.Store.RedeliverAsync(dead.Id, Entry("redeliver", dead.Id.ToString()), Ct)).ShouldBeTrue();

        (await world.Store.StepsAsync(succeeded.EventId, Confirm, Ct)).ShouldBeEmpty();
        (await world.Store.StepsAsync(dead.EventId, Confirm, Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A redeliver that is refused clears nothing.</summary>
    [Fact]
    public async Task A_refused_redeliver_keeps_the_steps()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var succeeded = await FinishedWithStepAsync(world, KeyA, dead: false);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.RedeliverAsync(succeeded.Id, Entry("redeliver", succeeded.Id.ToString()), Ct)).ShouldBeFalse("its lane is running");

        (await world.Store.StepsAsync(succeeded.EventId, Confirm, Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A replay clears the steps of the succeeded deliveries it resets, and keeps those of the dead ones.</summary>
    [Fact]
    public async Task A_replay_clears_the_steps_of_the_succeeded_deliveries_it_resets()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var succeeded = await FinishedWithStepAsync(world, KeyA, dead: false);
        var dead = await FinishedWithStepAsync(world, KeyB, dead: true);

        (await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Limit = 20 }, Entry("replay", Confirm), Ct)).ShouldNotBeNull().Affected.ShouldBe(2);

        (await world.Store.StepsAsync(succeeded.EventId, Confirm, Ct)).ShouldBeEmpty();
        (await world.Store.StepsAsync(dead.EventId, Confirm, Ct)).ShouldHaveSingleItem();
    }

    /// <summary>
    /// A replay leaves a succeeded delivery whose lane is running as it is; once the lane is free, a replay resets it and its
    /// steps are gone, so it runs every step again.
    /// </summary>
    [Fact]
    public async Task A_replay_resets_a_busy_lanes_succeeded_delivery_only_once_the_lane_is_free_and_then_without_its_steps()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var succeeded = await FinishedWithStepAsync(world, KeyA, dead: false);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        var running = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Limit = 20 }, Entry("replay", Confirm), Ct)).ShouldNotBeNull().Affected.ShouldBe(0);
        (await world.Store.FindAsync(succeeded.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("succeeded");

        await world.Store.CompleteAsync(Attempt(running), Ct);
        (await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Limit = 20 }, Entry("replay", $"{Confirm}-again"), Ct)).ShouldNotBeNull().Affected.ShouldBe(2);

        (await world.Store.FindAsync(succeeded.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("pending");
        (await world.Store.StepsAsync(succeeded.EventId, Confirm, Ct)).ShouldBeEmpty();
    }

    private static async Task<DateTimeOffset> FannedOutAtAsync(IDeliveryStoreWorld world) =>
        (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).Single().NotBefore;

    private static async Task<ClaimedDelivery> FinishedWithStepAsync(IDeliveryStoreWorld world, string key, bool dead)
    {
        var @event = await world.AppendEventAsync(key);
        await world.Store.FanOutAsync([Request(@event, Confirm, key)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).Single(delivery => delivery.EventId == @event);
        await world.Store.RecordStepAsync(Step(@event, Confirm, "charge", "1"), Ct);
        _ = dead
            ? await world.Store.FailAsync(Attempt(claimed), Failure("boom", null), Ct)
            : await world.Store.CompleteAsync(Attempt(claimed), Ct);

        return claimed;
    }

    private static DeliveryStep Step(Guid @event, string subscriber, string name, string result) =>
        new() { EventId = @event, Subscriber = subscriber, Name = name, Result = result, CompletedAt = _stepCompletedAt };
}
