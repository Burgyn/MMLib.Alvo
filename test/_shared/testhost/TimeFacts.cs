using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>What the time facts' handlers saw: the bookings created, and every reminder that fired.</summary>
public sealed class ReminderLog
{
    public ConcurrentQueue<AlvoEvent> Created { get; } = new();

    public ConcurrentQueue<AlvoEvent> Received { get; } = new();
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

    protected abstract Task<TimeDatabase> DatabaseAsync();

    private async Task<AlvoTestHost> StartAsync(ReminderLog? log = null)
    {
        var database = await DatabaseAsync();
        var seen = log ?? new ReminderLog();

        return await AlvoTestHost.StartAsync(alvo => Configure(alvo, database, seen), Time, cancellationToken: Ct);
    }

    private static void Configure(IAlvoBuilder alvo, TimeDatabase database, ReminderLog log)
    {
        database.Use(alvo);
        alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", BookingDesk));
        alvo.AddEventHandler("reminders", Reminder, (AlvoRecord _, AlvoEventContext context) => log.Received.Enqueue(context.Event));
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

    protected sealed record TimeDatabase(Action<IAlvoBuilder> Use);
}
