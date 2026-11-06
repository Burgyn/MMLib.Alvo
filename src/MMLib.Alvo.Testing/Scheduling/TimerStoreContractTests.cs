using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Scheduling;

/// <summary>
/// The protocol every <see cref="ITimerStore"/> implements identically on every engine: a timer is armed under a fresh
/// generation, claimed once per lease, and fired once — its delete fenced by that generation and committed with its event.
/// </summary>
/// <remarks>Inherited unchanged by both shipped drivers and by any external one; everything engine-specific arrives through <see cref="WorldAsync"/>.</remarks>
public abstract partial class TimerStoreContractTests
{
    private const string Due = "reminders.due";

    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    /// <summary>Builds a store over fresh, already-created storage.</summary>
    /// <returns>The world.</returns>
    protected abstract Task<ITimerStoreWorld> WorldAsync();

    /// <summary>Skips the test when the engine is unavailable on this runner; a no-op for an in-process engine.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A timer is claimable only once due, by one claim, and fires once.</summary>
    [Fact]
    public async Task A_due_timer_is_claimed_once_and_fired_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now.AddMinutes(5)), Ct);

        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();
        world.Advance(TimeSpan.FromMinutes(5));
        var claimed = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();
        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();

        (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event(Due), Ct)).ShouldBeTrue();
        (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event(Due), Ct)).ShouldBeFalse();
        (await world.OutboxTypesAsync()).ShouldBe([Due]);
    }

    /// <summary>A reschedule while a claim is in flight fences the stale fire; the new generation fires once at its own time.</summary>
    [Fact]
    public async Task A_reschedule_during_a_claim_fences_the_stale_fire_and_the_new_generation_fires_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var stale = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        await world.Store.UpsertAsync(Timer("t-1", world.Now.AddHours(1)), Ct);
        (await world.Store.FireAsync(stale.Scope, stale.Key, stale.Generation, Event(Due), Ct)).ShouldBeFalse();
        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();
        world.Advance(TimeSpan.FromHours(1));
        var fresh = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        fresh.DueAt.ShouldBe(world.Now);
        (await world.Store.FireAsync(fresh.Scope, fresh.Key, fresh.Generation, Event(Due), Ct)).ShouldBeTrue();
        (await world.OutboxTypesAsync()).ShouldHaveSingleItem();
    }

    /// <summary>A re-arm while a claim is in flight drops that claim's lease: the timer is claimable at its new time at once.</summary>
    [Fact]
    public async Task A_timer_re_armed_during_a_claim_is_claimable_at_once_under_its_new_generation()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var stale = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        var generation = await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);

        var fresh = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();
        fresh.Generation.ShouldBe(generation);
        fresh.Generation.ShouldNotBe(stale.Generation);
    }

    /// <summary>A cancel while a claim is in flight leaves its fire nothing to delete and nothing to append.</summary>
    [Fact]
    public async Task A_cancel_during_a_claim_leaves_the_fire_nothing_to_append()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var claimed = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        (await world.Store.CancelAsync(claimed.Scope, claimed.Key, Ct)).ShouldBeTrue();

        (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event(Due), Ct)).ShouldBeFalse();
        (await world.OutboxTypesAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// A cancel then a re-arm of the same key mints a new generation, so a claim of the cancelled timer cannot fire the new
    /// one early.
    /// </summary>
    [Fact]
    public async Task A_stale_claim_cannot_fire_a_timer_cancelled_and_rescheduled_under_the_same_key()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var stale = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        await world.Store.CancelAsync(stale.Scope, stale.Key, Ct);
        await world.Store.UpsertAsync(Timer("t-1", world.Now.AddHours(1)), Ct);

        (await world.Store.FireAsync(stale.Scope, stale.Key, stale.Generation, Event(Due), Ct)).ShouldBeFalse();
        (await world.Store.CountTimersAsync(Ct)).Pending.ShouldBe(1, "the re-armed timer is still there");
        (await world.OutboxTypesAsync()).ShouldBeEmpty();
    }

    /// <summary>One key in two scopes is two timers: cancelling one leaves the other armed.</summary>
    [Fact]
    public async Task The_same_key_in_two_scopes_is_two_timers()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now, scope: "a"), Ct);
        await world.Store.UpsertAsync(Timer("t-1", world.Now, scope: "b"), Ct);

        (await world.Store.CancelAsync("a", "t-1", Ct)).ShouldBeTrue();

        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem().Scope.ShouldBe("b");
    }

    /// <summary>A fire deletes the timer and appends its event, both committed.</summary>
    [Fact]
    public async Task A_fire_removes_the_timer_and_appends_its_event_in_one_commit()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var claimed = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

        (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event("reminders.fired"), Ct)).ShouldBeTrue();

        (await world.Store.CountTimersAsync(Ct)).Pending.ShouldBe(0);
        (await world.OutboxTypesAsync()).ShouldBe(["reminders.fired"]);
    }

    /// <summary>A fire whose append fails rolls its delete back, and the timer is claimable again once the lease ran out.</summary>
    [Fact]
    public async Task A_fire_whose_outbox_append_fails_leaves_the_timer_in_place()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        var claimed = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();
        await world.BreakOutboxAsync();

        await Should.ThrowAsync<Exception>(() => world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event(Due), Ct));

        (await world.Store.CountTimersAsync(Ct)).Pending.ShouldBe(1);
        world.Advance(_lease);
        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem().Generation.ShouldBe(claimed.Generation);
    }

    /// <summary>A lease that ran out makes a claimed timer claimable again — a crashed claimant's timer is not lost.</summary>
    [Fact]
    public async Task An_expired_lease_makes_a_timer_claimable_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
        await world.Store.ClaimTimersAsync(10, _lease, Ct);

        world.Advance(_lease - TimeSpan.FromSeconds(1));
        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();
        world.Advance(TimeSpan.FromSeconds(1));
        (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A claim takes the earliest due timers first, and no more than its batch.</summary>
    [Fact]
    public async Task A_claim_takes_the_earliest_due_first_and_at_most_the_batch()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("late", world.Now.AddMinutes(-1)), Ct);
        await world.Store.UpsertAsync(Timer("earliest", world.Now.AddMinutes(-3)), Ct);
        await world.Store.UpsertAsync(Timer("early", world.Now.AddMinutes(-2)), Ct);

        var claimed = await world.Store.ClaimTimersAsync(2, _lease, Ct);

        claimed.Select(timer => timer.Key).ShouldBe(["earliest", "early"]);
    }

    /// <summary>Every armed timer is pending; those whose time has come are also due.</summary>
    [Fact]
    public async Task Counts_split_pending_from_due()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.UpsertAsync(Timer("now", world.Now), Ct);
        await world.Store.UpsertAsync(Timer("past", world.Now.AddDays(-1)), Ct);
        await world.Store.UpsertAsync(Timer("later", world.Now.AddDays(1)), Ct);

        (await world.Store.CountTimersAsync(Ct)).ShouldBe(new TimerCounts { Pending = 3, Due = 2 });
    }

    /// <summary>Cancelling a key nothing armed answers that nothing was cancelled.</summary>
    [Fact]
    public async Task A_cancel_of_an_unknown_key_answers_false()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        (await world.Store.CancelAsync("", "never-armed", Ct)).ShouldBeFalse();
    }

    private static TimerEntry Timer(string key, DateTimeOffset due, string scope = "") =>
        TimerEntry.Of(scope, key, due, Event(Due), AlvoProvenance.None);

    private static AlvoCustomEvent Event(string type) =>
        AlvoCustomEnvelope.Build(type, "timers/t-1", null, AlvoContext.System(null), DateTimeOffset.UnixEpoch);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
