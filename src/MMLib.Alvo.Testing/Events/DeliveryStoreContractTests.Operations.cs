using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>The operator's half of the delivery protocol: reads, selection, redeliver, replay, pause and the operations log.</summary>
public abstract partial class DeliveryStoreContractTests
{
    /// <summary>A page lists deliveries newest first and continues strictly after the last id it returned.</summary>
    [Fact]
    public async Task A_page_lists_deliveries_newest_first_and_continues_after_the_last_one()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var events = new List<Guid>();
        foreach (var key in new[] { KeyA, KeyB, "tickets:c" })
        {
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key)], Ct);
            events.Add(@event);
        }

        var first = await world.Store.ListAsync(new DeliveryFilter { Limit = 2 }, Ct);
        var rest = await world.Store.ListAsync(new DeliveryFilter { Limit = 2, After = first[^1].Id }, Ct);

        first.Select(delivery => delivery.EventId).ShouldBe([events[2], events[1]]);
        rest.ShouldHaveSingleItem().EventId.ShouldBe(events[0]);
        first.ShouldAllBe(delivery => delivery.Payload == null && delivery.Status == "pending" && delivery.Kind == Kind);
    }

    /// <summary>A page filters by subscriber, by record, by status and by a half-open window of event ids.</summary>
    [Fact]
    public async Task A_page_filters_by_subscriber_record_status_and_event_window()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var early = await world.AppendEventAsync(KeyA);
        var middle = await world.AppendEventAsync(KeyB);
        var late = await world.AppendEventAsync("tickets:c");
        await world.Store.FanOutAsync([Request(early, Confirm, KeyA) with { DeadReason = "when-failed" }, Request(early, Audit, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(middle, Confirm, KeyB), Request(middle, Audit, KeyB)], Ct);
        await world.Store.FanOutAsync([Request(late, Confirm, "tickets:c")], Ct);

        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Audit, Limit = 10 }, Ct)).Count.ShouldBe(2);
        (await world.Store.ListAsync(new DeliveryFilter { PartitionKey = KeyB, Limit = 10 }, Ct)).Count.ShouldBe(2);
        (await world.Store.ListAsync(new DeliveryFilter { Status = "dead", Limit = 10 }, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(early);
        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Confirm, FromEventId = middle, ToEventId = late, Limit = 10 }, Ct))
            .ShouldHaveSingleItem().EventId.ShouldBe(middle, "the window includes its start and excludes its end");
    }

    /// <summary>One delivery is found with its envelope, and its attempts read back in order with what each ended as.</summary>
    [Fact]
    public async Task A_delivery_is_found_with_its_envelope_and_its_attempts_in_order()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(first), Failure("System.TimeoutException", TimeSpan.Zero), Ct);
        var second = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.CompleteAsync(Attempt(second), Ct);

        var found = (await world.Store.FindAsync(first.Id, Ct)).ShouldNotBeNull();
        var attempts = await world.Store.AttemptsAsync(first.Id, Ct);

        found.Status.ShouldBe("succeeded");
        found.Attempts.ShouldBe(2);
        found.Payload.ShouldNotBeNull().ShouldContain(@event.ToString());
        attempts.Select(attempt => (attempt.Attempt, attempt.Outcome, attempt.ErrorType)).ShouldBe(
            [(1, "failed", "System.TimeoutException"), (2, "succeeded", null)]);
    }

    /// <summary>An id no fan-out recorded is not found and has no attempts.</summary>
    [Fact]
    public async Task An_unknown_delivery_is_not_found_and_has_no_attempts()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        (await world.Store.FindAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
        (await world.Store.AttemptsAsync(Guid.NewGuid(), Ct)).ShouldBeEmpty();
    }

    /// <summary>A page asking for nothing is refused rather than answered empty.</summary>
    [Fact]
    public async Task A_page_asking_for_no_rows_is_refused()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Store.ListAsync(new DeliveryFilter { Limit = 0 }, Ct));
    }

    /// <summary>A page larger than the store's ceiling of 1 000 is refused, so no caller can ask the port for an unbounded read.</summary>
    [Fact]
    public async Task A_page_above_the_store_s_ceiling_is_refused()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Store.ListAsync(new DeliveryFilter { Limit = 1001 }, Ct));
        (await world.Store.ListAsync(new DeliveryFilter { Limit = 1000 }, Ct)).ShouldBeEmpty();
    }

    /// <summary>A cursor and a subscriber filter together continue within that subscriber's deliveries only.</summary>
    [Fact]
    public async Task A_page_of_one_subscriber_continues_after_its_cursor_within_that_subscriber()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var events = new List<Guid>();
        foreach (var key in new[] { KeyA, KeyB, "tickets:c" })
        {
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key), Request(@event, Audit, key)], Ct);
            events.Add(@event);
        }

        var first = await world.Store.ListAsync(new DeliveryFilter { Subscriber = Audit, Limit = 1 }, Ct);
        var rest = await world.Store.ListAsync(new DeliveryFilter { Subscriber = Audit, Limit = 10, After = first[^1].Id }, Ct);

        first.ShouldHaveSingleItem().EventId.ShouldBe(events[2]);
        rest.Select(delivery => delivery.EventId).ShouldBe([events[1], events[0]]);
        first.Concat(rest).ShouldAllBe(delivery => delivery.Subscriber == Audit);
    }

    /// <summary>A selection counts exactly, samples the first ids in event order, and changes nothing.</summary>
    [Fact]
    public async Task A_selection_counts_exactly_samples_in_event_order_and_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = new List<Guid>();
        foreach (var key in new[] { KeyA, KeyB, "tickets:c" })
        {
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key) with { DeadReason = "when-failed" }, Request(@event, Audit, key)], Ct);
            dead.Add(@event);
        }

        var before = await world.Store.ListAsync(new DeliveryFilter { Limit = 50 }, Ct);
        var selection = await world.Store.SelectAsync(new DeliveryFilter { Subscriber = Confirm, Status = "dead", Limit = 2 }, Ct);
        var after = await world.Store.ListAsync(new DeliveryFilter { Limit = 50 }, Ct);

        selection.Count.ShouldBe(3);
        selection.Sample.Count.ShouldBe(2);
        selection.Busy.ShouldBe(0);
        (await world.Store.FindAsync(selection.Sample[0], Ct)).ShouldNotBeNull().EventId.ShouldBe(dead[0]);
        after.ShouldBe(before);
    }

    /// <summary>
    /// A replayable selection keeps every finished status — succeeded, dead, cancelled — and leaves out what is unfinished and
    /// what broke a loop (dead <c>chain-depth-exceeded</c>); it counts as busy only a selected delivery whose record has a
    /// delivery of the <em>same</em> subscriber running — not another subscriber's on that record, nor the same subscriber's
    /// on another record.
    /// </summary>
    [Fact]
    public async Task A_replayable_selection_leaves_out_loop_breaks_and_counts_running_records()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var succeeded = await SucceededAsync(world, Audit, KeyA);
        var cancelled = await CancelledAsync(world, "retired-hook", "tickets:c");
        var loop = await world.AppendEventAsync(KeyA);
        var failed = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(loop, Confirm, KeyA) with { DeadReason = "chain-depth-exceeded" }], Ct);
        await world.Store.FanOutAsync([Request(failed, Confirm, KeyB) with { DeadReason = "when-failed" }], Ct);
        await RunningAsync(world, (Confirm, KeyB), (Confirm, KeyA), (Audit, KeyB));
        await world.Store.FanOutAsync([Request(await world.AppendEventAsync("tickets:c"), "notify-crm", "tickets:c")], Ct);

        var selection = await world.Store.SelectAsync(new DeliveryFilter { Replayable = true, Limit = 20 }, Ct);

        selection.Count.ShouldBe(3, "the succeeded, the cancelled and the failed delivery may be replayed");
        (await EventsOfAsync(world, selection.Sample)).ShouldBe([succeeded, cancelled, failed]);
        selection.Busy.ShouldBe(1, "only the failed delivery's record has a delivery of the same handler running");
    }

    /// <summary>A delivery of <paramref name="subscriber"/> on <paramref name="key"/> that ran and succeeded.</summary>
    private static async Task<Guid> SucceededAsync(IDeliveryStoreWorld world, string subscriber, string key)
    {
        var @event = await world.AppendEventAsync(key);
        await world.Store.FanOutAsync([Request(@event, subscriber, key)], Ct);
        var claimed = (await world.Store.ClaimAsync(Unordered(subscriber), Ct)).ShouldHaveSingleItem();
        (await world.Store.CompleteAsync(Attempt(claimed), Ct)).ShouldBeTrue();

        return @event;
    }

    /// <summary>A delivery of <paramref name="subscriber"/> on <paramref name="key"/> cancelled while pending.</summary>
    private static async Task<Guid> CancelledAsync(IDeliveryStoreWorld world, string subscriber, string key)
    {
        var @event = await world.AppendEventAsync(key);
        await world.Store.FanOutAsync([Request(@event, subscriber, key)], Ct);
        (await world.Store.CancelPendingAsync(subscriber, Ct)).ShouldBe(1);

        return @event;
    }

    /// <summary>One running delivery per subscriber and key, each of a fresh event.</summary>
    private static async Task RunningAsync(IDeliveryStoreWorld world, params (string Subscriber, string Key)[] lanes)
    {
        foreach (var (subscriber, key) in lanes)
        {
            await world.Store.FanOutAsync([Request(await world.AppendEventAsync(key), subscriber, key)], Ct);
        }

        var claimed = await world.Store.ClaimAsync(Unordered([.. lanes.Select(lane => lane.Subscriber).Distinct()]), Ct);
        claimed.Count.ShouldBe(lanes.Length);
    }

    /// <summary>The event each delivery delivers, in the deliveries' order.</summary>
    private static async Task<List<Guid>> EventsOfAsync(IDeliveryStoreWorld world, IReadOnlyList<Guid> deliveries)
    {
        var events = new List<Guid>();
        foreach (var id in deliveries)
        {
            events.Add((await world.Store.FindAsync(id, Ct)).ShouldNotBeNull().EventId);
        }

        return events;
    }

    /// <summary>A selection asking for nothing is refused rather than answered empty.</summary>
    [Fact]
    public async Task A_selection_asking_for_no_rows_is_refused()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Store.SelectAsync(new DeliveryFilter { Limit = 0 }, Ct));
    }

    /// <summary>A claim of unordered subscribers: their deliveries run whatever came before them in the lane.</summary>
    private static DeliveryClaim Unordered(params string[] subscribers) =>
        Claim() with { Ordered = [], Unordered = subscribers };

    private static readonly DateTimeOffset _operatedAt = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private const string Operator = "ops-key:00000000-0000-0000-0000-000000000001";

    /// <summary>Redelivering a dead delivery makes it the claimable head of its lane again, and the lane drains after it.</summary>
    [Fact]
    public async Task Redelivering_a_dead_delivery_makes_it_the_head_of_its_lane_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(dead, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the dead head blocks its lane");
        var id = (await world.Store.ListAsync(new DeliveryFilter { Status = "dead", Limit = 1 }, Ct)).ShouldHaveSingleItem().Id;

        (await world.Store.RedeliverAsync(id, Entry("redeliver", id.ToString()), Ct)).ShouldBeTrue();
        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.CompleteAsync(Attempt(head), Ct);

        head.EventId.ShouldBe(dead);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>
    /// A delivery that died at its last attempt runs again with a fresh attempt budget, and its history keeps the earlier
    /// attempts, numbered on.
    /// </summary>
    [Fact]
    public async Task A_redelivered_delivery_runs_again_with_a_fresh_budget_and_keeps_its_history()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(first), Failure("System.InvalidOperationException", retryAfter: null), Ct);

        await world.Store.RedeliverAsync(first.Id, Entry("redeliver", first.Id.ToString()), Ct);
        var again = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        (await world.Store.CompleteAsync(Attempt(again), Ct)).ShouldBeTrue();

        again.Attempts.ShouldBe(1);
        (await world.Store.AttemptsAsync(first.Id, Ct)).Select(attempt => (attempt.Attempt, attempt.Outcome)).ShouldBe([(1, "dead"), (2, "succeeded")]);
        (await world.Store.FindAsync(first.Id, Ct)).ShouldNotBeNull().LastErrorType.ShouldBeNull();
    }

    /// <summary>
    /// A claim's late answer never lands on a later claim of the same delivery: the attempt count restarts with a redeliver,
    /// so only the claimant — unique to each claim — tells the two apart.
    /// </summary>
    [Fact]
    public async Task A_late_answer_from_before_a_redeliver_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var stale = (await world.Store.ClaimAsync(Claim(Confirm) with { Claimant = "claim-1" }, Ct)).ShouldHaveSingleItem();
        await world.Store.DeadLetterPendingAsync(Confirm, "retired", Ct);
        await world.Store.RedeliverAsync(stale.Id, Entry("redeliver", stale.Id.ToString()), Ct);
        var fresh = (await world.Store.ClaimAsync(Claim(Confirm) with { Claimant = "claim-2" }, Ct)).ShouldHaveSingleItem();

        var answered = await world.Store.CompleteAsync(
            new DeliveryAttempt { Delivery = stale, Claimant = "claim-1", StartedAt = _operatedAt, Duration = TimeSpan.FromSeconds(1) }, Ct);

        answered.ShouldBeFalse("the first claim's answer must not finish the second claim's run");
        fresh.Attempts.ShouldBe(stale.Attempts, "the attempt count alone cannot tell the two claims apart");
        (await world.Store.FindAsync(stale.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("running");
    }

    /// <summary>A delivery still pending, running or retrying is not redelivered, and nothing is logged.</summary>
    [Fact]
    public async Task Redelivering_an_unfinished_delivery_changes_nothing_and_logs_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var pending = (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).ShouldHaveSingleItem();
        (await world.Store.RedeliverAsync(pending.Id, Entry("redeliver", pending.Id.ToString()), Ct)).ShouldBeFalse();
        var running = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.RedeliverAsync(running.Id, Entry("redeliver", running.Id.ToString()), Ct)).ShouldBeFalse();
        (await world.Store.RedeliverAsync(Guid.NewGuid(), Entry("redeliver", "unknown"), Ct)).ShouldBeFalse();

        running.Attempts.ShouldBe(1, "the pending redeliver did not reset a claim count it never had");
        (await world.Store.FindAsync(running.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("running");
        (await world.OperationsAsync()).ShouldBeEmpty();
    }

    /// <summary>A finished delivery whose record has a delivery of the same handler running is not redelivered: two would run at once.</summary>
    [Fact]
    public async Task Redelivering_while_its_record_runs_another_delivery_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var failed = await world.AppendEventAsync(KeyA);
        var running = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(failed, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(running, Confirm, KeyA)], Ct);
        await world.Store.ClaimAsync(Unordered(Confirm), Ct);
        var dead = (await world.Store.ListAsync(new DeliveryFilter { Status = "dead", Limit = 1 }, Ct)).ShouldHaveSingleItem();

        (await world.Store.RedeliverAsync(dead.Id, Entry("redeliver", dead.Id.ToString()), Ct)).ShouldBeFalse();

        (await world.Store.FindAsync(dead.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("dead");
        (await world.OperationsAsync()).ShouldBeEmpty();
    }

    /// <summary>A redeliver that changed the delivery writes exactly one log row, with who, what and why.</summary>
    [Fact]
    public async Task A_redeliver_writes_one_log_row()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        var id = (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).ShouldHaveSingleItem().Id;
        var entry = Entry("redeliver", id.ToString());

        await world.Store.RedeliverAsync(id, entry, Ct);

        var row = (await world.OperationsAsync()).ShouldHaveSingleItem();
        (row.Id, row.At, row.Actor, row.Operation, row.Target, row.Reason, row.Affected)
            .ShouldBe((entry.Id, entry.At, entry.Actor, "redeliver", id.ToString(), "contract", (int?)1));
    }

    private static DeliveryOperationEntry Entry(string operation, string target, string? key = null) => new()
    {
        Id = AlvoEventId.Create(_operatedAt),
        At = _operatedAt,
        Actor = Operator,
        Operation = operation,
        Target = target,
        Reason = "contract",
        IdempotencyScope = key is null ? null : "global/ops",
        IdempotencyKey = key,
        Fingerprint = key is null ? null : "fingerprint",
    };
}
