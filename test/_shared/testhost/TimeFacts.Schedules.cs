using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Every run a schedule fact's jobs saw, in the order they ran.</summary>
public sealed class ScheduleLog
{
    public ConcurrentQueue<AlvoJobContext> Runs { get; } = new();
}

/// <summary>Records each run.</summary>
public sealed class RecordingScheduledJob(ScheduleLog log) : IAlvoScheduledJob
{
    public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        log.Runs.Enqueue(context);
        return Task.CompletedTask;
    }
}

/// <summary>Records each run, and fails the first occurrence it is ever handed.</summary>
public sealed class FailingFirstScheduledJob(ScheduleLog log) : IAlvoScheduledJob
{
    public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        log.Runs.Enqueue(context);
        return log.Runs.Count == 1 ? throw new InvalidOperationException("the first night failed") : Task.CompletedTask;
    }
}

/// <summary>Adds <c>tenant:note</c> for every booking its run can read, so a run that read another tenant's row shows.</summary>
public sealed class NoteCountingJob(ConcurrentBag<string> seen) : IAlvoScheduledJob
{
    public async Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        var bookings = await context.Data.QueryAsync(new AlvoQuery { Entity = "bookings" }, cancellationToken);
        foreach (var booking in bookings.Items)
        {
            seen.Add($"{context.Tenant}:{booking["note"]}");
        }
    }
}

/// <summary>The tenants a fact's host serves.</summary>
public sealed class FixedTenants(params TenantId[] tenants) : IAlvoTenantCatalog
{
    public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantId>>(tenants);
}

/// <summary>Cron schedules over the real pipeline: one run per occurrence, a missed one once, per tenant on <c>EachTenant</c>.</summary>
/// <remarks>
/// The clock starts at 01:00 UTC the day after the timer facts' start, so a <c>0 2 * * *</c> schedule falls due one hour in.
/// Every fact drains once before it advances the clock, so the start-time sync is seen to fire nothing.
/// </remarks>
public abstract partial class TimeFacts
{
    private const string Nightly = "nightly-roll";
    private const string Desk = "booking-desk";
    private static readonly DateTimeOffset _oneAm = new(2026, 11, 7, 1, 0, 0, TimeSpan.Zero);

    protected TenantId TenantA { get; } = new(Guid.NewGuid());

    protected TenantId TenantB { get; } = new(Guid.NewGuid());

    [Fact]
    public async Task A_due_occurrence_runs_once_as_a_schedule_delivery()
    {
        var log = new ScheduleLog();
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(log));
        await alvo.DrainEventsAsync(Ct);
        log.Runs.ShouldBeEmpty();

        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        var run = log.Runs.ShouldHaveSingleItem();
        (run.Id, run.ScheduledFor, run.Tenant, run.Attempt).ShouldBe((Nightly, _oneAm.AddHours(1), (TenantId?)null, 1));
        (await Deliveries(alvo, Nightly)).ShouldHaveSingleItem().Kind.ShouldBe("schedule");
    }

    [Fact]
    public async Task A_missed_occurrence_fires_once_and_resumes_from_now()
    {
        var log = new ScheduleLog();
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(log));
        await alvo.DrainEventsAsync(Ct);

        Time.Advance(TimeSpan.FromDays(3));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        log.Runs.ShouldHaveSingleItem().ScheduledFor.ShouldBe(_oneAm.AddHours(1));
        (await TimerStore(alvo).SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt.ShouldBe(_oneAm.AddDays(3).AddHours(1));
    }

    [Fact]
    public async Task A_failing_run_does_not_block_the_next_occurrence()
    {
        var log = new ScheduleLog();
        await using var alvo = await StartScheduledAsync(alvo => alvo
            .AddSchedule<FailingFirstScheduledJob>(Nightly, "0 2 * * *", configure: options => options.Retry = AlvoRetryPolicy.None)
            .Services.AddSingleton(log));
        await alvo.DrainEventsAsync(Ct);

        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromDays(1));
        await alvo.DrainEventsAsync(Ct);

        log.Runs.Select(run => run.ScheduledFor).ShouldBe([_oneAm.AddHours(1), _oneAm.AddDays(1).AddHours(1)]);
        (await Deliveries(alvo, Nightly)).Select(delivery => delivery.Status).Order(StringComparer.Ordinal).ShouldBe(["dead", "succeeded"]);
    }

    [Fact]
    public async Task An_each_tenant_schedule_runs_once_per_tenant_and_reads_only_its_own_rows()
    {
        var seen = new ConcurrentBag<string>();
        await using var alvo = await StartScheduledAsync(alvo => alvo
            .AddSchedule<NoteCountingJob>("count-notes", "0 2 * * *", configure: o => o.Scope = AlvoScheduleScope.EachTenant)
            .Services.AddSingleton<IAlvoTenantCatalog>(new FixedTenants(TenantA, TenantB)).AddSingleton(seen));
        await SeedBookingAsync(alvo, TenantA, "a-note");
        await SeedBookingAsync(alvo, TenantB, "b-note");
        await alvo.DrainEventsAsync(Ct);

        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe([$"{TenantA}:a-note", $"{TenantB}:b-note"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_each_tenant_schedule_without_a_tenant_catalog_refuses_the_start()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartScheduledAsync(alvo => alvo
            .AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *", configure: o => o.Scope = AlvoScheduleScope.EachTenant)
            .Services.AddSingleton(new ScheduleLog())));

        refusal.Message.ShouldContain($"extensions/{Nightly}/scope");
        refusal.FixSuggestion.ShouldContain("IAlvoTenantCatalog");
    }

    [Fact]
    public async Task A_retired_schedule_is_removed_at_the_next_start()
    {
        var database = await DatabaseAsync();
        await using (var first = await StartScheduledAsync(
            alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(new ScheduleLog()), database))
        {
            (await TimerStore(first).SchedulesAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(Nightly);
        }

        await using var second = await StartScheduledAsync(alvo => alvo.AddRetiredExtension(Nightly, AlvoRetirement.Discard), database);

        (await TimerStore(second).SchedulesAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// A web host and a worker on one database: the web host registers no schedule. Its start removes nothing and its rounds
    /// claim nothing, so the worker's schedule keeps running — on the worker.
    /// </summary>
    [Fact]
    public async Task A_process_without_the_schedule_neither_removes_nor_runs_it()
    {
        var log = new ScheduleLog();
        var database = await DatabaseAsync();
        await using var worker = await StartScheduledAsync(
            alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(log), database);
        await using var web = await StartScheduledAsync(_ => { }, database);
        await worker.DrainEventsAsync(Ct);
        (await TimerStore(web).SchedulesAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(Nightly);

        Time.Advance(TimeSpan.FromHours(1));
        (await web.DrainEventsAsync(Ct)).Claimed.ShouldBe(0);
        log.Runs.ShouldBeEmpty();
        await worker.DrainEventsAsync(Ct);

        log.Runs.ShouldHaveSingleItem().ScheduledFor.ShouldBe(_oneAm.AddHours(1));
    }

    [Fact]
    public async Task Run_now_creates_a_delivery_that_runs_without_moving_the_next_occurrence()
    {
        var log = new ScheduleLog();
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(log));
        await alvo.DrainEventsAsync(Ct);
        var next = (await TimerStore(alvo).SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt;

        var delivery = await AsOperator(alvo, "ops").RunScheduleAsync(Desk, Nightly, Ct);
        (delivery.Status, delivery.Subscriber).ShouldBe(("pending", Nightly));
        await alvo.DrainEventsAsync(Ct);

        log.Runs.ShouldHaveSingleItem().ScheduledFor.ShouldBe(_oneAm);
        (await TimerStore(alvo).SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt.ShouldBe(next);
    }

    [Fact]
    public async Task Run_now_of_an_unknown_schedule_is_404()
    {
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(new ScheduleLog()));

        await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => AsOperator(alvo, "ops").RunScheduleAsync(Desk, "weekly-report", Ct));
    }

    [Fact]
    public async Task A_schedule_cannot_be_switched_off_and_the_refusal_says_how_to_run_it()
    {
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(new ScheduleLog()));

        var refusal = await Should.ThrowAsync<ManagementConflictException>(() => AsOperator(alvo, "boss").SetEnabledAsync(Desk, Nightly, false, "maintenance", Ct));

        refusal.Message.ShouldContain($"'{Nightly}' is a schedule");
        refusal.Message.ShouldContain($"schedules/{Nightly}/run");
    }

    [Fact]
    public async Task Run_now_needs_developer()
    {
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(new ScheduleLog()));

        await Should.ThrowAsync<ManagementForbiddenException>(() => AsOperator(alvo, "watch").RunScheduleAsync(Desk, Nightly, Ct));
    }

    [Fact]
    public async Task The_catalog_shows_next_and_last_run_last_outcome_and_timer_counts()
    {
        var log = new ScheduleLog();
        await using var alvo = await StartScheduledAsync(alvo => alvo.AddSchedule<RecordingScheduledJob>(Nightly, "0 2 * * *").Services.AddSingleton(log));
        await Scheduler(alvo).ScheduleAsync("remind-later", _oneAm.AddDays(1), Reminder, Data("b1"), Tenant, Ct);
        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);

        AsOperator(alvo, "watch");
        var catalog = await alvo.Services.GetRequiredService<IAlvoManagement>().GetExtensionsAsync(Desk, Ct);

        var schedule = catalog.Items.Single(item => item.Id == Nightly);
        (schedule.NextRunAt, schedule.LastRunAt, schedule.LastOutcome).ShouldBe((_oneAm.AddDays(1).AddHours(1), _oneAm.AddHours(1), "succeeded"));
        catalog.Timers.ShouldBe(new ManagementTimerCounts { Pending = 1, Due = 0 });
    }

    [Fact]
    public async Task Run_now_on_each_tenant_answers_the_delivery_of_the_first_tenants_event()
    {
        var seen = new ConcurrentBag<string>();
        await using var alvo = await StartScheduledAsync(alvo => alvo
            .AddSchedule<NoteCountingJob>("count-notes", "0 2 * * *", configure: o => o.Scope = AlvoScheduleScope.EachTenant)
            .Services.AddSingleton<IAlvoTenantCatalog>(new FixedTenants(TenantA, TenantB)).AddSingleton(seen));
        await alvo.DrainEventsAsync(Ct);

        var answered = await AsOperator(alvo, "ops").RunScheduleAsync(Desk, "count-notes", Ct);

        var deliveries = await Deliveries(alvo, "count-notes");
        deliveries.Count.ShouldBe(2);
        var first = deliveries.Single(delivery => delivery.TenantId == TenantA.ToString());
        answered.EventId.ShouldBe(first.EventId);
        deliveries.Max(delivery => delivery.Id).ShouldNotBe(first.Id, "the second tenant's delivery is the newer one");
    }

    private static IAlvoOperations AsOperator(AlvoTestHost alvo, string role)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller(role),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private async Task<AlvoTestHost> StartScheduledAsync(Action<IAlvoBuilder> configure, TimeDatabase? database = null)
    {
        if (Time.GetUtcNow() < _oneAm)
        {
            Time.SetUtcNow(_oneAm);
        }

        var used = database ?? await DatabaseAsync();

        return await AlvoTestHost.StartAsync(
            alvo =>
            {
                used.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", BookingDesk));
                configure(alvo);
            },
            Time,
            cancellationToken: Ct);
    }

    private static async Task SeedBookingAsync(AlvoTestHost alvo, TenantId tenant, string note) =>
        await alvo.Data.CreateAsync(
            "bookings",
            new Dictionary<string, object?> { ["note"] = note, ["starts_at"] = _oneAm.AddDays(1), ["tenant_id"] = tenant.Value },
            Staff(tenant),
            cancellationToken: Ct);

    private static async Task<IReadOnlyList<StoredDelivery>> Deliveries(AlvoTestHost alvo, string subscriber) =>
        await alvo.Services.GetRequiredService<IDeliveryStore>().ListAsync(new DeliveryFilter { Subscriber = subscriber, Limit = 50 }, Ct);
}
