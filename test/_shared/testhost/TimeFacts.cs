using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>What the time facts' handlers saw: the bookings created, and every reminder that fired.</summary>
public sealed class ReminderLog
{
    public ConcurrentQueue<AlvoEvent> Created { get; } = new();

    public ConcurrentQueue<AlvoEvent> Received { get; } = new();

    public ConcurrentQueue<BookingReminderDue> Typed { get; } = new();
}

[AlvoEvent("bookings.typed_reminder_due")]
public sealed record BookingReminderDue(Guid BookingId, DateTimeOffset StartsAt);

public sealed class TypedReminderHandler(ReminderLog log) : IAlvoEventHandler<BookingReminderDue>
{
    public Task HandleAsync(BookingReminderDue message, AlvoEventContext context, CancellationToken cancellationToken)
    {
        log.Typed.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Records the timer pump's <c>TimerUnbuildable</c> lines.</summary>
internal sealed class TimerLogCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _unbuildable = new();

    internal IReadOnlyList<string> Unbuildable => [.. _unbuildable];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (eventId.Name == "TimerUnbuildable")
        {
            _unbuildable.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Timers over the real pipeline (spec 2026-10-14 §7 HX-13), on whichever engine the deriving class supplies — the SQLite leg
/// in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
/// <remarks>Every fact runs on a fake clock: a timer fires when a fact advances it and drains, never on a slow runner.</remarks>
public abstract partial class TimeFacts
{
    protected const string Reminder = "bookings.reminder_due";
    private const string BookingDesk = "booking-desk.alvo.json";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Time { get; } = new(_now);

    protected TenantId Tenant { get; } = new(Guid.NewGuid());

    [Fact]
    public async Task A_timer_scheduled_in_a_rolled_back_unit_leaves_no_row()
    {
        await using var alvo = await StartAsync();
        var staff = Staff(Tenant);
        var armed = false;

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Transactions(alvo).TransactAsync(staff, async (unit, ct) =>
        {
            await Scheduler(alvo).ScheduleAsync("remind-1", _now.AddHours(24), Reminder, null, cancellationToken: ct);
            armed = true;
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (armed, failure.Message).ShouldBe((true, "the host changed its mind"));
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
    }

    [Fact]
    public async Task A_timer_scheduled_in_a_committed_unit_takes_the_units_tenant()
    {
        await using var alvo = await StartAsync();
        var staff = Staff(Tenant);

        await Transactions(alvo).TransactAsync(staff, async (unit, ct) =>
        {
            await unit.CreateAsync("bookings", Booking("b1"), ct);
            await Scheduler(alvo).ScheduleAsync("remind-1", _now, Reminder, Data("b1"), cancellationToken: ct);
        }, cancellationToken: Ct);
        await Should.ThrowAsync<ArgumentException>(() => Transactions(alvo).TransactAsync(staff, async (unit, ct) =>
        {
            await unit.CreateAsync("bookings", Booking("b2"), ct);
            await Scheduler(alvo).ScheduleAsync("remind-2", _now, Reminder, Data("b2"), new TenantId(Guid.NewGuid()), ct);
        }, cancellationToken: Ct));

        var claimed = (await TimerStore(alvo).ClaimTimersAsync(10, TimeSpan.FromMinutes(1), Ct)).ShouldHaveSingleItem();
        (claimed.Scope, claimed.Key).ShouldBe((Tenant.ToString(), "remind-1"));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "bookings" }, staff, Ct)).Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_fake_clock_and_a_drain_deliver_a_24_hour_reminder()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log);
        await Scheduler(alvo).ScheduleAsync("remind-b1", _now.AddHours(24), Reminder, Data("b1"), Tenant, Ct);

        await alvo.DrainEventsAsync(Ct);
        log.Received.ShouldBeEmpty();
        Time.Advance(TimeSpan.FromHours(24));
        await alvo.DrainEventsAsync(Ct);

        var fired = log.Received.ShouldHaveSingleItem();
        fired.Data.Record.ShouldNotBeNull()["booking"].ShouldBe("b1");
        (fired.Subject, fired.TenantId, fired.AuthType).ShouldBe(("timers/remind-b1", Tenant.ToString(), AlvoEventAuthType.System));
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
    }

    /// <summary>
    /// Depth arithmetic, stated: the created event is at depth <c>c</c>, its handler runs at <c>c + 1</c> and arms the timer
    /// at that depth; the pump fires the stored depth + 1, so the fired event is at <c>c + 2</c>.
    /// </summary>
    [Fact]
    public async Task A_timer_scheduled_in_a_handler_fires_with_the_handlers_correlation_causation_and_depth()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log, TimerMode.ArmOnCreate);
        await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromHours(24));
        await alvo.DrainEventsAsync(Ct);

        var origin = log.Created.ShouldHaveSingleItem();
        var fired = log.Received.ShouldHaveSingleItem();
        (fired.CorrelationId, fired.CausationId, fired.ChainDepth).ShouldBe((origin.CorrelationId, origin.Id.ToString(), origin.ChainDepth + 2));
    }

    [Fact]
    public async Task A_rescheduled_reminder_fires_once_at_its_new_time_even_when_it_races_a_claim()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log);
        await Scheduler(alvo).ScheduleAsync("remind-b1", _now, Reminder, Data("b1"), Tenant, Ct);
        (await TimerStore(alvo).ClaimTimersAsync(10, TimeSpan.FromMinutes(1), Ct)).ShouldHaveSingleItem();

        await Scheduler(alvo).ScheduleAsync("remind-b1", _now.AddHours(2), Reminder, Data("b1-moved"), Tenant, Ct);
        await alvo.DrainEventsAsync(Ct);
        log.Received.ShouldBeEmpty();
        Time.Advance(TimeSpan.FromHours(2));
        await alvo.DrainEventsAsync(Ct);

        var fired = log.Received.ShouldHaveSingleItem();
        fired.Data.Record.ShouldNotBeNull()["booking"].ShouldBe("b1-moved");
        fired.Time.ShouldBe(_now.AddHours(2));
    }

    [Fact]
    public async Task A_reminder_cancelled_while_claimed_never_fires()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log);
        await Scheduler(alvo).ScheduleAsync("remind-b1", _now, Reminder, Data("b1"), Tenant, Ct);
        (await TimerStore(alvo).ClaimTimersAsync(10, TimeSpan.FromMinutes(1), Ct)).ShouldHaveSingleItem();

        (await Scheduler(alvo).CancelAsync("remind-b1", Tenant, Ct)).ShouldBeTrue();
        Time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);

        log.Received.ShouldBeEmpty();
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
    }

    [Fact]
    public async Task A_timer_survives_a_restart_of_the_host()
    {
        var log = new ReminderLog();
        var database = await DatabaseAsync();
        await using (var first = await StartAsync(new ReminderLog(), database: database))
        {
            await Scheduler(first).ScheduleAsync("remind-b1", _now.AddHours(24), Reminder, Data("b1"), Tenant, Ct);
        }

        await using var second = await StartAsync(log, database: database);
        Time.Advance(TimeSpan.FromHours(24));
        await second.DrainEventsAsync(Ct);

        log.Received.ShouldHaveSingleItem().Data.Record.ShouldNotBeNull()["booking"].ShouldBe("b1");
    }

    [Fact]
    public async Task A_typed_reminder_round_trips_a_guid_and_a_timestamp()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log);
        var sent = new BookingReminderDue(Guid.NewGuid(), new DateTimeOffset(2026, 11, 7, 10, 30, 0, TimeSpan.Zero));

        await Scheduler(alvo).ScheduleAsync("typed-b1", _now, sent, Tenant, Ct);
        await alvo.DrainEventsAsync(Ct);

        var received = log.Typed.ShouldHaveSingleItem();
        (received.BookingId, received.StartsAt).ShouldBe((sent.BookingId, sent.StartsAt));
    }

    /// <summary>
    /// Armed outside any flow the first timer is stored at depth 0 and fires at 1; each handler run (at event depth + 1) re-arms
    /// at its own depth, so handlers run at 2, 4, 6 and 8 — and the event fired at 9 is dead at fan-out, not run.
    /// </summary>
    [Fact]
    public async Task A_self_re_arming_timer_stops_at_the_depth_limit()
    {
        var log = new ReminderLog();
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(log, TimerMode.ReArm, database);
        await Scheduler(alvo).ScheduleAsync("loop", _now, Reminder, Data("loop"), Tenant, Ct);

        for (var round = 0; round < 10; round++)
        {
            await alvo.DrainEventsAsync(Ct);
            Time.Advance(TimeSpan.FromHours(1));
        }

        log.Received.Select(fired => fired.ChainDepth + 1).ShouldBe([2, 4, 6, 8]);
        var dead = (await DeliveryRows.ReadAsync(database.Connect, Ct)).Where(row => row.Status == "dead").ShouldHaveSingleItem();
        dead.LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_timer_due_in_the_past_fires_on_the_next_drain()
    {
        var log = new ReminderLog();
        await using var alvo = await StartAsync(log);
        var anHourAgoInBratislava = _now.AddHours(-1).ToOffset(TimeSpan.FromHours(2));

        await Scheduler(alvo).ScheduleAsync("late", anHourAgoInBratislava, Reminder, Data("late"), Tenant, Ct);
        await alvo.DrainEventsAsync(Ct);

        log.Received.ShouldHaveSingleItem().Data.Record.ShouldNotBeNull()["booking"].ShouldBe("late");
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("provenance")]
    public async Task A_stored_timer_that_cannot_be_built_is_cancelled_not_retried(string corrupted)
    {
        var log = new ReminderLog();
        var capture = new TimerLogCapture();
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(log, database: database, logs: capture);
        await Scheduler(alvo).ScheduleAsync("broken", _now, Reminder, Data("b1"), Tenant, Ct);
        await ExecuteAsync(database.Connect, $"UPDATE alvo_timers SET {corrupted} = '{{not json'");

        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        log.Received.ShouldBeEmpty();
        (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
        capture.Unbuildable.ShouldHaveSingleItem().ShouldContain("broken");
    }

    protected abstract Task<TimeDatabase> DatabaseAsync();

    private async Task<AlvoTestHost> StartAsync(
        ReminderLog? log = null, TimerMode mode = TimerMode.Record, TimeDatabase? database = null, TimerLogCapture? logs = null)
    {
        var used = database ?? await DatabaseAsync();
        var seen = log ?? new ReminderLog();

        return await AlvoTestHost.StartAsync(alvo => Configure(alvo, used, seen, mode, logs), Time, cancellationToken: Ct);
    }

    private void Configure(IAlvoBuilder alvo, TimeDatabase database, ReminderLog log, TimerMode mode, TimerLogCapture? logs)
    {
        database.Use(alvo);
        alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", BookingDesk));
        alvo.Services.AddSingleton(log);
        if (logs is not null)
        {
            alvo.Services.AddSingleton<ILoggerProvider>(logs);
        }

        alvo.AddEventHandler("reminders", Reminder, ReminderHandler(log, mode));
        alvo.AddEventHandler<BookingReminderDue, TypedReminderHandler>("typed-reminders");
        if (mode is TimerMode.ArmOnCreate)
        {
            alvo.Entity("bookings", bookings => bookings.OnCreated("arm-reminders", ArmOnCreate(log)));
        }
    }

    private Func<AlvoRecord, AlvoEventContext, IAlvoScheduler, CancellationToken, Task> ReminderHandler(ReminderLog log, TimerMode mode) =>
        async (_, context, scheduler, ct) =>
        {
            log.Received.Enqueue(context.Event);
            if (mode is TimerMode.ReArm)
            {
                await scheduler.ScheduleAsync("loop", Time.GetUtcNow().AddHours(1), Reminder, Data("loop"), TenantOf(context), ct);
            }
        };

    private Func<AlvoCreated<AlvoRecord>, AlvoEventContext, IAlvoScheduler, CancellationToken, Task> ArmOnCreate(ReminderLog log) =>
        async (_, context, scheduler, ct) =>
        {
            log.Created.Enqueue(context.Event);
            await scheduler.ScheduleAsync("remind-created", Time.GetUtcNow().AddHours(24), Reminder, Data("created"), TenantOf(context), ct);
        };

    private static TenantId TenantOf(AlvoEventContext context) => TenantId.Parse(context.Event.TenantId!, null);

    private static async Task ExecuteAsync(Func<DbConnection> connect, string sql)
    {
        await using var connection = connect();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static IAlvoTransactions Transactions(AlvoTestHost alvo) => alvo.Services.GetRequiredService<IAlvoTransactions>();

    private static IAlvoScheduler Scheduler(AlvoTestHost alvo) => alvo.Services.GetRequiredService<IAlvoScheduler>();

    private static ITimerStore TimerStore(AlvoTestHost alvo) => alvo.Services.GetRequiredService<ITimerStore>();

    private static AlvoContext Staff(TenantId tenant) => AlvoTestHost.Caller("staff") with { Tenant = tenant };

    private Dictionary<string, object?> Booking(string note) => new()
    {
        ["note"] = note,
        ["starts_at"] = _now.AddDays(1),
        ["tenant_id"] = Tenant.Value,
    };

    private static Dictionary<string, object?> Data(string booking) => new() { ["booking"] = booking };

    protected enum TimerMode
    {
        Record,
        ArmOnCreate,
        ReArm,
    }

    protected sealed record TimeDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
