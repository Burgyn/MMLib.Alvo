using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;


namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// The descriptor's <c>timers</c> over the real pipeline (spec 2026-10-14 §7 HX-18 acceptance (3)): armed, moved and cancelled
/// in the write's own transaction, fired through the timer pump. On whichever engine the deriving class supplies.
/// </summary>
/// <remarks>Every fact runs on a fake clock: a timer fires when a fact advances it and drains, never on a slow runner.</remarks>
public abstract class EntityTimerFacts
{
    private const string Reminder = "bookings.reminder_due";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Time { get; } = new(_now);

    protected TenantId Tenant { get; } = new(Guid.NewGuid());

    [Fact]
    public async Task A_reminder_re_arms_when_starts_at_moves()
    {
        EnsureEngineAvailable();
        var log = new ReminderLog();
        await using var alvo = await StartAsync(await DatabaseAsync(), log);
        var id = await ConfirmedAsync(alvo, _now.AddHours(48));

        await UpdateAsync(alvo, id, new() { ["starts_at"] = _now.AddHours(72) });
        await AdvanceAndDrainAsync(alvo, TimeSpan.FromHours(25));
        log.Received.ShouldBeEmpty();
        await AdvanceAndDrainAsync(alvo, TimeSpan.FromHours(24));

        var fired = log.Received.ShouldHaveSingleItem();
        fired.Data.Record.ShouldNotBeNull()["id"].ShouldBe(id.ToString("D"));
        (fired.Subject, fired.TenantId).ShouldBe(($"timers/alvo:bookings:reminder:{id:D}", Tenant.ToString()));
    }

    [Fact]
    public async Task A_reminder_is_cancelled_when_the_status_leaves_confirmed()
    {
        EnsureEngineAvailable();
        var log = new ReminderLog();
        await using var alvo = await StartAsync(await DatabaseAsync(), log);
        var id = await ConfirmedAsync(alvo, _now.AddHours(48));

        await UpdateAsync(alvo, id, new() { ["status"] = "cancelled" });
        await AdvanceAndDrainAsync(alvo, TimeSpan.FromHours(48));

        log.Received.ShouldBeEmpty();
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
    }

    [Fact]
    public async Task A_rolled_back_write_leaves_no_timer()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync(), new ReminderLog());

        await Should.ThrowAsync<InvalidOperationException>(() => alvo.Services.GetRequiredService<IAlvoTransactions>().TransactAsync(Staff(), async (unit, ct) =>
        {
            var created = await unit.CreateAsync(StateMachineFacts.Bookings, Booking(_now.AddHours(48)), ct);
            await unit.UpdateAsync(StateMachineFacts.Bookings, (Guid)created["id"]!, new Dictionary<string, object?> { ["status"] = "confirmed" }, cancellationToken: ct);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = StateMachineFacts.Bookings }, Staff(), Ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_move_racing_a_claimed_fire_fires_once_at_the_new_time()
    {
        EnsureEngineAvailable();
        var log = new ReminderLog();
        await using var alvo = await StartAsync(await DatabaseAsync(), log);
        var id = await ConfirmedAsync(alvo, _now.AddHours(48));
        Time.Advance(TimeSpan.FromHours(24));
        var claimed = (await TimerStore(alvo).ClaimTimersAsync(10, TimeSpan.FromMinutes(1), Ct)).ShouldHaveSingleItem();

        await UpdateAsync(alvo, id, new() { ["starts_at"] = _now.AddHours(72) });

        (await TimerStore(alvo).FireAsync(claimed.Scope, claimed.Key, claimed.Generation, StaleFire(claimed), Ct)).ShouldBeFalse();
        await alvo.DrainEventsAsync(Ct);
        log.Received.ShouldBeEmpty();
        await AdvanceAndDrainAsync(alvo, TimeSpan.FromHours(24));
        log.Received.ShouldHaveSingleItem().Data.Record.ShouldNotBeNull()["at"].ShouldBe("2026-11-08T09:00:00.0000000+00:00");
    }

    [Fact]
    public async Task Deleting_the_record_cancels_its_timers()
    {
        EnsureEngineAvailable();
        await using var alvo = await StartAsync(await DatabaseAsync(), new ReminderLog());
        var id = await ConfirmedAsync(alvo, _now.AddHours(48));
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(1);

        await alvo.Data.DeleteAsync(StateMachineFacts.Bookings, id, Staff(), cancellationToken: Ct);

        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
    }

    [Fact]
    public async Task The_fired_event_carries_the_writers_correlation()
    {
        EnsureEngineAvailable();
        var log = new ReminderLog();
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, log);
        await ConfirmedAsync(alvo, _now.AddHours(48));
        var confirming = await UpdatedEventAsync(database);

        await AdvanceAndDrainAsync(alvo, TimeSpan.FromHours(24));

        var fired = log.Received.ShouldHaveSingleItem();
        (fired.CorrelationId, fired.CausationId).ShouldBe((confirming.CorrelationId, confirming.Id.ToString()));
    }

    protected abstract Task<LifecycleDatabase> DatabaseAsync();

    /// <summary>Skips the fact where the deriving engine cannot run; every fact calls it first, before any assertion can catch the skip.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private async Task<AlvoTestHost> StartAsync(LifecycleDatabase database, ReminderLog log) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", StateMachineFacts.LifecycleDesk));
                alvo.AddEventHandler("reminders", Reminder, (Func<AlvoRecord, AlvoEventContext, CancellationToken, Task>)((_, context, _) =>
                {
                    log.Received.Enqueue(context.Event);
                    return Task.CompletedTask;
                }));
            },
            Time,
            cancellationToken: Ct);

    private async Task AdvanceAndDrainAsync(AlvoTestHost alvo, TimeSpan by)
    {
        Time.Advance(by);
        await alvo.DrainEventsAsync(Ct);
    }

    private async Task<Guid> ConfirmedAsync(AlvoTestHost alvo, DateTimeOffset startsAt)
    {
        var id = (Guid)(await alvo.Data.CreateAsync(StateMachineFacts.Bookings, Booking(startsAt), Staff(), cancellationToken: Ct))["id"]!;
        await UpdateAsync(alvo, id, new() { ["status"] = "confirmed" });

        return id;
    }

    private Task<AlvoRecord> UpdateAsync(AlvoTestHost alvo, Guid id, Dictionary<string, object?> values) =>
        alvo.Data.UpdateAsync(StateMachineFacts.Bookings, id, values, Staff(), cancellationToken: Ct);

    private static async Task<AlvoEvent> UpdatedEventAsync(LifecycleDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM alvo_outbox WHERE event_type = 'entity.bookings.updated'";

        return AlvoEventJson.Read((string)(await command.ExecuteScalarAsync(Ct))!);
    }

    private static AlvoCustomEvent StaleFire(ClaimedTimer claimed) => AlvoCustomEvent.Create(AlvoEventJson.Read(claimed.Payload));

    private static ITimerStore TimerStore(AlvoTestHost alvo) => alvo.Services.GetRequiredService<ITimerStore>();

    private AlvoContext Staff() => AlvoTestHost.Caller("staff") with { Tenant = Tenant };

    private Dictionary<string, object?> Booking(DateTimeOffset startsAt) => new()
    {
        ["starts_at"] = startsAt,
        ["note"] = "booked",
        ["tenant_id"] = Tenant.Value,
    };
}
