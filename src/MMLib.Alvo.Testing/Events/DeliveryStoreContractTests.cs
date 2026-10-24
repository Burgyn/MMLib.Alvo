using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// The delivery protocol every <see cref="IDeliveryStore"/> implements identically on every engine: fan-out, claim,
/// per-lane order, retry, the attempt ceiling, lease recovery and fenced completion.
/// </summary>
/// <remarks>Inherited unchanged by both shipped drivers and by any external one; everything engine-specific arrives through <see cref="WorldAsync"/>.</remarks>
public abstract class DeliveryStoreContractTests
{
    private const string Kind = "handler";
    private const string Claimant = "worker-1";
    private const string Confirm = "send-confirmation";
    private const string Audit = "audit-trail";
    private const string OtherClaimant = "worker-2";
    private const string KeyA = "tickets:a";
    private const string KeyB = "tickets:b";
    private const int MaxAttempts = 7;
    private const int OutboxCeiling = 10;

    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _outboxLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _retry = TimeSpan.FromSeconds(5);

    /// <summary>Builds a store over fresh, already-created storage.</summary>
    /// <returns>The world.</returns>
    protected abstract Task<IDeliveryStoreWorld> WorldAsync();

    /// <summary>Skips the test when the engine is unavailable on this runner; a no-op for an in-process engine.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A fan-out records one pending delivery per subscriber; a claim takes them with their envelope.</summary>
    [Fact]
    public async Task A_fan_out_records_one_delivery_per_subscriber_and_a_claim_takes_them_with_their_envelope()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct)).ShouldBe(2);
        var claimed = await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct);

        claimed.Select(delivery => delivery.Subscriber).ShouldBe([Audit, Confirm]);
        claimed.ShouldAllBe(delivery => delivery.EventId == @event && delivery.Attempts == 1);
        claimed.ShouldAllBe(delivery => delivery.Payload != null && delivery.Payload.Contains(@event.ToString()));
    }

    /// <summary>Re-fanning one event out records nothing: the (event, subscriber) pair is unique.</summary>
    [Fact]
    public async Task A_second_fan_out_of_one_event_records_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct)).ShouldBe(0);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A claim takes only the subscribers it names — the replica's registered ones.</summary>
    [Fact]
    public async Task A_claim_takes_only_the_subscribers_it_names()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Subscriber.ShouldBe(Confirm);
    }

    /// <summary>A lane is claimed one event at a time, in event order.</summary>
    [Fact]
    public async Task A_lane_is_claimed_one_event_at_a_time_in_event_order()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA)], Ct);

        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        head.EventId.ShouldBe(first);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the head is running, so the lane waits");

        (await world.Store.CompleteAsync(Attempt(head), Ct)).ShouldBeTrue();
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(second);
    }

    /// <summary>A dead delivery blocks the later deliveries of its lane, and of its lane only.</summary>
    [Fact]
    public async Task A_dead_delivery_blocks_its_own_lane_and_no_other()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var deadA = await world.AppendEventAsync(KeyA);
        var laterA = await world.AppendEventAsync(KeyA);
        var laterB = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(deadA, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(laterA, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(laterB, Confirm, KeyB)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(laterB);
    }

    /// <summary>An unordered subscriber is not blocked by a dead earlier delivery of its lane.</summary>
    [Fact]
    public async Task An_unordered_subscriber_is_not_blocked_by_a_dead_earlier_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(dead, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        var claim = new DeliveryClaim { Claimant = Claimant, Ordered = [], Unordered = [Confirm], BatchSize = 10, Lease = _lease, OutboxMaxAttempts = OutboxCeiling, OutboxClaimLease = _outboxLease };

        (await world.Store.ClaimAsync(claim, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A failed delivery waits for its retry; once due it is claimed again, with the attempt counted.</summary>
    [Fact]
    public async Task A_failed_delivery_waits_for_its_retry_before_it_is_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.FailAsync(Attempt(first), Failure("System.TimeoutException", _retry), Ct)).ShouldBeTrue();

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("not before its retry is due");
        world.Advance(_retry + TimeSpan.FromSeconds(1));
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>A delivery whose last attempt fails is dead, never claimed again, and still blocks its lane.</summary>
    [Fact]
    public async Task A_delivery_is_dead_once_its_last_attempt_fails()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
            claimed.EventId.ShouldBe(@event);
            claimed.Attempts.ShouldBe(attempt);
            await world.Store.FailAsync(Attempt(claimed), Failure("boom", _retry), Ct);
            world.Advance(_retry + TimeSpan.FromSeconds(1));
        }

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("dead after attempt 7, and the dead head blocks its lane");
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBe([Confirm], "the later event still waits");
    }

    /// <summary>A failure with no retry is dead at once.</summary>
    [Fact]
    public async Task A_failure_without_a_retry_is_dead_at_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        await world.Store.FailAsync(Attempt(claimed), Failure("chain-depth-exceeded", null), Ct);
        world.Advance(TimeSpan.FromDays(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A dead request is recorded dead: never claimed, and not pending work.</summary>
    [Fact]
    public async Task A_dead_request_is_recorded_dead()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "chain-depth-exceeded" }], Ct)).ShouldBe(1);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A running delivery whose lease expired is claimed again — the crash-recovery path.</summary>
    [Fact]
    public async Task A_running_delivery_whose_lease_expired_is_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.ClaimAsync(Claim(Confirm) with { Claimant = "dead-worker" }, Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the lease still holds");
        world.Advance(_lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>A late finisher whose lease was reclaimed changes nothing; the reclaiming claimant's answer stands.</summary>
    [Fact]
    public async Task A_late_finisher_whose_lease_was_reclaimed_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var late = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        world.Advance(_lease + TimeSpan.FromSeconds(1));
        var current = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.CompleteAsync(Attempt(late), Ct)).ShouldBeFalse("same claimant, older attempt");
        (await world.Store.FailAsync(Attempt(late), Failure("boom", null), Ct)).ShouldBeFalse();
        (await world.Store.CompleteAsync(Attempt(current), Ct)).ShouldBeTrue();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A completed delivery is final.</summary>
    [Fact]
    public async Task A_completed_delivery_is_never_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.CompleteAsync(Attempt((await world.Store.ClaimAsync(Claim(Confirm), Ct)).Single()), Ct);

        world.Advance(TimeSpan.FromDays(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>A claim returns lane heads in event order, and takes no more than its batch.</summary>
    [Fact]
    public async Task A_claim_returns_lane_heads_in_event_order_up_to_its_batch()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var events = new List<Guid>();
        foreach (var key in new[] { "tickets:1", "tickets:2", "tickets:3" })
        {
            events.Add(await world.AppendEventAsync(key));
        }

        for (var index = events.Count - 1; index >= 0; index--)
        {
            await world.Store.FanOutAsync([Request(events[index], Confirm, $"tickets:{index + 1}")], Ct);
        }

        (await world.Store.ClaimAsync(Claim(Confirm) with { BatchSize = 2 }, Ct)).Select(delivery => delivery.EventId)
            .ShouldBe(events.Take(2));
    }

    /// <summary>
    /// Bringing the storage up is idempotent, and storage just brought up holds no work: the boot calls
    /// <see cref="IDeliveryStore.EnsureAsync"/> and then reads and retires before anything has been fanned out.
    /// </summary>
    [Fact]
    public async Task Storage_brought_up_again_holds_no_work_and_retiring_over_it_ends_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await world.Store.EnsureAsync(Ct);
        await world.Store.EnsureAsync(Ct);

        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
        (await world.Store.DeadLetterPendingAsync(Confirm, "retired", Ct)).ShouldBe(0);
        (await world.Store.CancelPendingAsync(Audit, Ct)).ShouldBe(0);
        (await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct)).ShouldBeEmpty();
    }

    /// <summary>Retiring ends a subscriber's unfinished work: dead-lettered or cancelled, and no longer pending.</summary>
    [Fact]
    public async Task Retiring_ends_a_subscriber_s_unfinished_work()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct);

        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBe([Audit, Confirm], ignoreOrder: true);
        (await world.Store.DeadLetterPendingAsync(Confirm, "retired", Ct)).ShouldBe(1);
        (await world.Store.CancelPendingAsync(Audit, Ct)).ShouldBe(1);

        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
        (await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// An ordered delivery waits for every earlier event of its record the outbox has not fanned out yet — a failed fan-out,
    /// a later batch, a dispatcher that died between claim and fan-out — and goes once that event is dispatched.
    /// </summary>
    [Fact]
    public async Task An_ordered_delivery_waits_for_an_earlier_event_of_its_record_that_is_not_fanned_out_yet()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var earlier = await world.AppendUnfannedEventAsync(KeyA, attempts: 1);
        var later = await world.AppendEventAsync(KeyA);
        var otherKey = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(otherKey, Confirm, KeyB)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(otherKey, "only the other record's lane is free");
        await world.MarkDispatchedAsync(earlier);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>An earlier event the outbox gave up on (at its attempt ceiling) will never be fanned out, so it blocks nothing.</summary>
    [Fact]
    public async Task An_earlier_event_past_the_outbox_ceiling_holds_no_lane_back()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA, attempts: OutboxCeiling);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>An unordered subscriber does not wait for an earlier event that is not fanned out.</summary>
    [Fact]
    public async Task An_unordered_subscriber_does_not_wait_for_an_earlier_unfanned_event()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        var claim = Claim() with { Unordered = [Confirm] };

        (await world.Store.ClaimAsync(claim, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>
    /// An earlier event on its <b>last</b> attempt is already counted at the ceiling while it is being fanned out; under a
    /// live claim it still holds the lane, or its own delivery could land after a later one ran. The claim is the outbox's,
    /// so it lives as long as the outbox's lease, not the delivery lease.
    /// </summary>
    [Fact]
    public async Task An_earlier_event_on_its_last_attempt_under_a_live_claim_holds_the_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA, attempts: OutboxCeiling, claimed: true);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        world.Advance(_lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>The same entry, once its claim is stale, was abandoned at the ceiling: it will never be fanned out, so it stops blocking.</summary>
    [Fact]
    public async Task An_earlier_event_at_the_ceiling_under_a_stale_claim_holds_no_lane_back()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA, attempts: OutboxCeiling, claimed: true);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        world.Advance(_outboxLease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A dead <c>unbound</c> delivery records missed work; it is replayed explicitly and does not hold its lane.</summary>
    [Fact]
    public async Task A_dead_unbound_delivery_does_not_block_its_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var missed = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(missed, Confirm, KeyA) with { DeadReason = DeliveryRequest.UnboundReason }], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A delivery whose envelope the outbox no longer holds is still claimed — with no payload, for the runner to dead-letter.</summary>
    [Fact]
    public async Task A_delivery_whose_outbox_entry_is_gone_is_claimed_without_a_payload()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.FanOutAsync([Request(Guid.NewGuid(), Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Payload.ShouldBeNull();
    }

    /// <summary>A cancelled delivery is finished: it holds its lane back no more than a succeeded one does.</summary>
    [Fact]
    public async Task A_cancelled_delivery_does_not_block_its_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var cancelled = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(cancelled, Audit, KeyA)], Ct);
        await world.Store.CancelPendingAsync(Audit, Ct);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Audit, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Audit), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A dead request stays dead: fanning the same (event, subscriber) out again records nothing.</summary>
    [Fact]
    public async Task A_second_fan_out_does_not_revive_a_dead_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct)).ShouldBe(0);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>A lane is one subscriber's: a dead delivery of one subscriber holds back no other subscriber of the same record.</summary>
    [Fact]
    public async Task A_lane_is_one_subscriber_s_and_blocks_no_other_subscriber_of_the_record()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(first, Audit, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyA), Request(second, Audit, KeyA)], Ct);

        var claimed = (await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct)).ShouldHaveSingleItem();

        claimed.Subscriber.ShouldBe(Confirm);
        claimed.EventId.ShouldBe(second);
    }

    /// <summary>An attempt is answered for once, and only by the claimant holding it: another claimant's answer and a second answer change nothing.</summary>
    [Fact]
    public async Task An_attempt_is_answered_for_once_and_only_by_its_claimant()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.CompleteAsync(Attempt(claimed) with { Claimant = OtherClaimant }, Ct)).ShouldBeFalse("another claimant does not hold it");
        (await world.Store.CompleteAsync(Attempt(claimed), Ct)).ShouldBeTrue();
        (await world.Store.CompleteAsync(Attempt(claimed), Ct)).ShouldBeFalse("the attempt is already answered for");
        (await world.Store.FailAsync(Attempt(claimed), Failure("boom", _retry), Ct)).ShouldBeFalse("a second answer does not flip the first");

        world.Advance(_retry + TimeSpan.FromSeconds(1));
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("it succeeded, and stays succeeded");
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A delivery cancelled while it runs stays cancelled: its handler's late answer changes nothing.</summary>
    [Fact]
    public async Task A_delivery_cancelled_while_it_runs_is_not_revived_by_its_late_answer()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.CancelPendingAsync(Confirm, Ct)).ShouldBe(1, "a running delivery is unfinished work too");
        (await world.Store.FailAsync(Attempt(claimed), Failure("boom", _retry), Ct)).ShouldBeFalse();
        (await world.Store.CompleteAsync(Attempt(claimed), Ct)).ShouldBeFalse();

        world.Advance(_retry + TimeSpan.FromSeconds(1));
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>A delivery dead-lettered while it runs stays dead: a late success does not free the lane it holds.</summary>
    [Fact]
    public async Task A_delivery_dead_lettered_while_it_runs_is_not_revived_by_its_late_answer()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.DeadLetterPendingAsync(Confirm, "retired", Ct)).ShouldBe(1);
        (await world.Store.CompleteAsync(Attempt(claimed), Ct)).ShouldBeFalse();
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the dead delivery still holds its lane");
    }

    /// <summary>
    /// Only an <b>earlier</b> outbox entry holds a lane back: the delivery's own entry, still undispatched while it is being
    /// fanned out, and any later entry of the same record do not.
    /// </summary>
    [Fact]
    public async Task An_undispatched_entry_at_or_after_the_delivery_s_own_event_holds_nothing_back()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var own = await world.AppendUnfannedEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(own, Confirm, KeyA)], Ct);
        await world.AppendUnfannedEventAsync(KeyA);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(own);
    }

    /// <summary>An unclaimed earlier entry one attempt below the outbox ceiling may still be fanned out, so it holds the lane.</summary>
    [Fact]
    public async Task An_earlier_event_one_attempt_below_the_outbox_ceiling_holds_the_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA, attempts: OutboxCeiling - 1);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>A request repeated within one fan-out is recorded once: the (event, subscriber) pair is unique there too.</summary>
    [Fact]
    public async Task A_request_repeated_within_one_fan_out_is_recorded_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Confirm, KeyA)], Ct)).ShouldBe(1);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A claim for no deliveries is refused rather than answered with nothing.</summary>
    [Fact]
    public async Task A_claim_for_no_deliveries_is_refused()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await world.Store.ClaimAsync(Claim(Confirm) with { BatchSize = 0 }, Ct));
    }

    /// <summary>Two claimants claiming at once take disjoint deliveries, and between them every lane head.</summary>
    [Fact]
    public async Task Two_claimants_claiming_at_once_take_disjoint_deliveries()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        foreach (var key in Enumerable.Range(1, 6).Select(index => $"tickets:{index}"))
        {
            await world.Store.FanOutAsync([Request(await world.AppendEventAsync(key), Confirm, key)], Ct);
        }

        var claims = await Task.WhenAll(
            world.Store.ClaimAsync(Claim(Confirm), Ct),
            world.Store.ClaimAsync(Claim(Confirm) with { Claimant = OtherClaimant }, Ct));

        var claimed = claims.SelectMany(batch => batch).Select(delivery => delivery.Id).ToList();
        claimed.ShouldBeUnique();
        claimed.Count.ShouldBe(6);
    }

    private static DeliveryRequest Request(Guid @event, string subscriber, string key) =>
        new() { EventId = @event, Subscriber = subscriber, PartitionKey = key, Kind = Kind };

    private static DeliveryClaim Claim(params string[] ordered) =>
        new() { Claimant = Claimant, Ordered = ordered, BatchSize = 10, Lease = _lease, OutboxMaxAttempts = OutboxCeiling, OutboxClaimLease = _outboxLease };

    private static DeliveryAttempt Attempt(ClaimedDelivery delivery) =>
        new() { Delivery = delivery, Claimant = Claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(5) };

    private static DeliveryFailure Failure(string errorType, TimeSpan? retryAfter) =>
        new() { ErrorType = errorType, RetryAfter = retryAfter, MaxAttempts = MaxAttempts };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
