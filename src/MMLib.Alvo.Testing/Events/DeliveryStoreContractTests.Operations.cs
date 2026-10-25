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
    /// A replayable selection leaves out what is unfinished and what broke a loop (dead <c>chain-depth-exceeded</c>), and
    /// counts the selected deliveries whose record has a delivery of the same subscriber running.
    /// </summary>
    [Fact]
    public async Task A_replayable_selection_leaves_out_loop_breaks_and_counts_running_records()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var loop = await world.AppendEventAsync(KeyA);
        var failed = await world.AppendEventAsync(KeyB);
        var running = await world.AppendEventAsync(KeyB);
        var pending = await world.AppendEventAsync("tickets:c");
        await world.Store.FanOutAsync([Request(loop, Confirm, KeyA) with { DeadReason = "chain-depth-exceeded" }], Ct);
        await world.Store.FanOutAsync([Request(failed, Confirm, KeyB) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(running, Confirm, KeyB)], Ct);
        await world.Store.FanOutAsync([Request(pending, Audit, "tickets:c")], Ct);
        (await world.Store.ClaimAsync(Unordered(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(running);

        var selection = await world.Store.SelectAsync(new DeliveryFilter { Replayable = true, Limit = 20 }, Ct);

        selection.Count.ShouldBe(1, "only the failed delivery may be replayed");
        (await world.Store.FindAsync(selection.Sample[0], Ct)).ShouldNotBeNull().EventId.ShouldBe(failed);
        selection.Busy.ShouldBe(1, "its record has another delivery of the same handler running");
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
}
