using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Scheduling;

/// <summary>
/// The schedule and data-migration half of the protocol: a sync makes the stored schedules exactly the registered ones, an
/// occurrence is leased by one claimant and advanced once with its events, and a migration starts once with its mark.
/// </summary>
public abstract partial class TimerStoreContractTests
{
    private const string Nightly = "nightly";
    private const string NightlyEvent = "alvo.schedule.nightly";
    private const string TwoAm = "0 2 * * *";
    private const string ThreeAm = "0 3 * * *";

    /// <summary>Claimants racing one due occurrence: one advances it, once, with one event.</summary>
    [Fact]
    public async Task Racing_claimants_advance_one_occurrence_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now)], Ct);
        using var start = new Barrier(3);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => Task.Run(async () =>
        {
            start.SignalAndWait(Ct);
            var claimed = await world.Store.ClaimSchedulesAsync($"worker-{index}", _lease, Ct);
            return claimed.Count == 1 && await world.Store.AdvanceScheduleAsync(Advance($"worker-{index}", claimed[0], world.Now.AddDays(1)), Ct);
        }, Ct)));

        outcomes.Count(advanced => advanced).ShouldBe(1);
        (await world.OutboxTypesAsync()).ShouldBe([NightlyEvent]);
        (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt.ShouldBe(world.Now.AddDays(1));
    }

    /// <summary>A claimant whose lease ran out and was taken over cannot advance; the one that took it over can, once.</summary>
    [Fact]
    public async Task An_advance_by_a_claimant_whose_lease_was_taken_over_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now)], Ct);
        var first = (await world.Store.ClaimSchedulesAsync("worker-1", _lease, Ct)).ShouldHaveSingleItem();

        world.Advance(_lease);
        var second = (await world.Store.ClaimSchedulesAsync("worker-2", _lease, Ct)).ShouldHaveSingleItem();

        (await world.Store.AdvanceScheduleAsync(Advance("worker-1", first, world.Now.AddDays(1)), Ct)).ShouldBeFalse();
        (await world.Store.AdvanceScheduleAsync(Advance("worker-2", second, world.Now.AddDays(1)), Ct)).ShouldBeTrue();
        (await world.OutboxTypesAsync()).ShouldHaveSingleItem();
    }

    /// <summary>An advance whose append fails rolls back: the occurrence is still the next run.</summary>
    [Fact]
    public async Task An_advance_whose_outbox_append_fails_leaves_the_occurrence_due()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now)], Ct);
        var claimed = (await world.Store.ClaimSchedulesAsync("worker-1", _lease, Ct)).ShouldHaveSingleItem();
        await world.BreakOutboxAsync();

        await Should.ThrowAsync<Exception>(() => world.Store.AdvanceScheduleAsync(Advance("worker-1", claimed, world.Now.AddDays(1)), Ct));

        var stored = (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem();
        stored.NextRunAt.ShouldBe(world.Now);
        stored.LastRunAt.ShouldBeNull();
    }

    /// <summary>An occurrence run in each of three tenants appends three events in its one commit, and records its last run.</summary>
    [Fact]
    public async Task An_advance_appends_one_event_per_tenant_in_one_commit()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now)], Ct);
        var claimed = (await world.Store.ClaimSchedulesAsync("worker-1", _lease, Ct)).ShouldHaveSingleItem();
        var advance = Advance("worker-1", claimed, world.Now.AddDays(1)) with
        {
            Events = [.. Enumerable.Range(0, 3).Select(_ => Framework(NightlyEvent, new TenantId(Guid.NewGuid())))],
        };

        (await world.Store.AdvanceScheduleAsync(advance, Ct)).ShouldBeTrue();

        (await world.OutboxTypesAsync()).ShouldBe([NightlyEvent, NightlyEvent, NightlyEvent]);
        (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem().LastRunAt.ShouldBe(claimed.ScheduledFor);
    }

    /// <summary>A re-sync with the same cron keeps the stored next run; a changed cron moves it.</summary>
    [Fact]
    public async Task Syncing_a_changed_cron_moves_the_next_run_and_an_unchanged_one_keeps_it()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var t1 = world.Now.AddHours(1);
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, t1)], Ct);

        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now.AddHours(2))], Ct);
        (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt.ShouldBe(t1);

        var t3 = world.Now.AddHours(3);
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, t3) with { Cron = ThreeAm }], Ct);
        var stored = (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem();
        stored.NextRunAt.ShouldBe(t3);
        stored.Cron.ShouldBe(ThreeAm);
    }

    /// <summary>A sync deletes every stored schedule it does not list; an empty sync empties the store.</summary>
    [Fact]
    public async Task Syncing_drops_schedules_no_longer_registered()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule("a", world.Now), Schedule("b", world.Now)], Ct);

        await world.Store.SyncSchedulesAsync([Schedule("a", world.Now)], Ct);
        (await world.Store.SchedulesAsync(Ct)).Select(schedule => schedule.Id).ShouldBe(["a"]);

        await world.Store.SyncSchedulesAsync([], Ct);
        (await world.Store.SchedulesAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>An occurrence still in the future is not claimed.</summary>
    [Fact]
    public async Task A_schedule_not_yet_due_is_not_claimed()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, world.Now.AddMinutes(1))], Ct);

        (await world.Store.ClaimSchedulesAsync("worker-1", _lease, Ct)).ShouldBeEmpty();
        world.Advance(TimeSpan.FromMinutes(1));
        (await world.Store.ClaimSchedulesAsync("worker-1", _lease, Ct)).ShouldHaveSingleItem().Id.ShouldBe(Nightly);
    }

    /// <summary>A run now appends its events and leaves the schedule's next and last run where they were.</summary>
    [Fact]
    public async Task A_run_now_appends_its_events_without_moving_the_schedule()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var next = world.Now.AddHours(5);
        await world.Store.SyncSchedulesAsync([Schedule(Nightly, next)], Ct);

        await world.Store.AppendRunAsync([Framework(NightlyEvent), Framework(NightlyEvent)], Ct);

        (await world.OutboxTypesAsync()).ShouldBe([NightlyEvent, NightlyEvent]);
        var stored = (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem();
        stored.NextRunAt.ShouldBe(next);
        stored.LastRunAt.ShouldBeNull();
    }

    /// <summary>A migration starts once — at whatever revision first started it — with its events and its mark in one commit.</summary>
    [Fact]
    public async Task A_migration_starts_once_and_appends_its_events_with_its_mark()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        (await world.Store.StartMigrationAsync("backfill", 3, [Framework("alvo.migration.backfill")], Ct)).ShouldBeTrue();
        (await world.Store.StartMigrationAsync("backfill", 4, [Framework("alvo.migration.backfill")], Ct)).ShouldBeFalse();

        (await world.OutboxTypesAsync()).ShouldBe(["alvo.migration.backfill"]);
        (await world.Store.StartedMigrationsAsync(Ct)).ShouldBe(["backfill"]);
        (await world.MigrationRowsAsync()).ShouldBe([("backfill", 3)]);
    }

    /// <summary>A migration whose append fails is not marked, so the next round starts it again.</summary>
    [Fact]
    public async Task A_migration_whose_outbox_append_fails_is_not_marked()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.BreakOutboxAsync();

        await Should.ThrowAsync<Exception>(() => world.Store.StartMigrationAsync("backfill", 3, [Framework("alvo.migration.backfill")], Ct));

        (await world.MigrationRowsAsync()).ShouldBeEmpty();
        (await world.Store.StartedMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    private static ScheduleDefinition Schedule(string id, DateTimeOffset next) =>
        new() { Id = id, Cron = TwoAm, TimeZone = "UTC", NextRunAt = next };

    private static ScheduleAdvance Advance(string claimant, ClaimedSchedule claimed, DateTimeOffset next) => new()
    {
        Id = claimed.Id,
        Claimant = claimant,
        ScheduledFor = claimed.ScheduledFor,
        NextRunAt = next,
        Events = [Framework(NightlyEvent)],
    };

    private static AlvoFrameworkEnvelope Framework(string type, TenantId? tenant = null) => AlvoFrameworkEvent.Create(
        type,
        $"schedules/{Nightly}",
        new Dictionary<string, object?> { ["schedule"] = Nightly },
        AlvoContext.System(tenant),
        DateTimeOffset.UnixEpoch);
}
