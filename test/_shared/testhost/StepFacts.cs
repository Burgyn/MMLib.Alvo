using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>What the step facts' handlers did: how often each step's work ran, and what step three saw.</summary>
public sealed class StepLog
{
    private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.Ordinal);
    private int _failStepThree;

    public int FailStepThreeTimes
    {
        get => Volatile.Read(ref _failStepThree);
        init => _failStepThree = value;
    }

    public ConcurrentQueue<string> SeenByThree { get; } = new();

    public int Runs(string step) => _runs.GetValueOrDefault(step);

    internal void Ran(string step) => _runs.AddOrUpdate(step, 1, (_, runs) => runs + 1);

    internal bool FailsStepThree() => Interlocked.Decrement(ref _failStepThree) >= 0;
}

/// <summary>
/// Steps over the real pipeline (spec 2026-10-14 §7 HX-14), on whichever engine the deriving class supplies — the SQLite leg
/// in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
/// <remarks>Every fact runs on a fake clock: a retry or a wake-up is due when a fact advances it and drains.</remarks>
public abstract partial class StepFacts
{
    protected const string Handler = "confirm-booking";
    private const string BookingDesk = "booking-desk.alvo.json";

    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static DateTimeOffset Now => _now;

    protected FakeTimeProvider Time { get; } = new(_now);

    protected TenantId Tenant { get; } = new(Guid.NewGuid());

    [Fact]
    public async Task A_failure_at_step_three_does_not_re_run_steps_one_and_two()
    {
        var log = new StepLog { FailStepThreeTimes = 1 };
        await using var alvo = await StartAsync(log, ThreeSteps(log));
        await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        (log.Runs("one"), log.Runs("two"), log.Runs("three")).ShouldBe((1, 1, 2));
        log.SeenByThree.ShouldBe(["deposit-1:email-1", "deposit-1:email-1"]);
        (await DeliveriesAsync(alvo)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_step_name_used_twice_in_one_run_fails_the_attempt()
    {
        var log = new StepLog();
        await using var alvo = await StartAsync(log, Twice(log));
        await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        log.Runs("one").ShouldBe(1);
        var failed = (await DeliveriesAsync(alvo)).ShouldHaveSingleItem();
        (failed.Status, failed.LastErrorType).ShouldBe(("retrying", typeof(InvalidOperationException).FullName));
    }

    [Fact]
    public async Task Two_handlers_of_one_event_keep_their_own_steps()
    {
        var log = new StepLog();
        await using var alvo = await StartAsync(log, OwnStep("first", log), builder => builder.Entity("bookings", bookings =>
            bookings.OnCreated("audit-booking", OwnStep("second", log), configure: Fast)));
        await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await alvo.DrainEventsAsync(Ct);

        (log.Runs("first"), log.Runs("second")).ShouldBe((1, 1));
        log.SeenByThree.Order(StringComparer.Ordinal).ShouldBe(["first", "first", "second", "second"]);
        var deliveries = await DeliveriesAsync(alvo);
        deliveries.Count.ShouldBe(2);
        foreach (var delivery in deliveries)
        {
            (await Store(alvo).StepsAsync(delivery.EventId, delivery.Subscriber, Ct)).ShouldHaveSingleItem().Name.ShouldBe("one");
        }
    }

    [Fact]
    public async Task A_fixture_run_uses_the_pass_through()
    {
        var log = new StepLog();
        await using var alvo = await StartAsync(log, ThreeSteps(log));
        await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        var delivered = (await DeliveriesAsync(alvo)).ShouldHaveSingleItem();
        var fixture = await FixtureAsync(alvo, delivered);

        await alvo.DeliverAsync(fixture, Ct);
        await alvo.DeliverAsync(fixture, Ct);

        (log.Runs("one"), log.Runs("two"), log.Runs("three")).ShouldBe((3, 3, 3));
        (await Store(alvo).StepsAsync(delivered.EventId, Handler, Ct)).Count.ShouldBe(3);
    }

    protected abstract Task<StepDatabase> DatabaseAsync();

    protected async Task<AlvoTestHost> StartAsync(
        StepLog log, Func<AlvoCreated<AlvoRecord>, AlvoEventContext, CancellationToken, Task> handler, Action<IAlvoBuilder>? more = null)
    {
        var database = await DatabaseAsync();

        return await AlvoTestHost.StartAsync(alvo =>
        {
            database.Use(alvo);
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", BookingDesk));
            alvo.Services.AddSingleton(log);
            alvo.Entity("bookings", bookings => bookings.OnCreated(Handler, handler, configure: Fast));
            more?.Invoke(alvo);
        }, Time, cancellationToken: Ct);
    }

    protected static void Fast(AlvoHandlerOptions options) => options.Retry = AlvoRetryPolicy.From(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    protected static IDeliveryStore Store(AlvoTestHost alvo) => alvo.Services.GetRequiredService<IDeliveryStore>();

    protected static async Task<IReadOnlyList<StoredDelivery>> DeliveriesAsync(AlvoTestHost alvo) =>
        await Store(alvo).ListAsync(new DeliveryFilter { Limit = 100 }, Ct);

    protected static async Task<IReadOnlyList<StoredDeliveryAttempt>> HistoryAsync(AlvoTestHost alvo) =>
        await Store(alvo).AttemptsAsync((await DeliveriesAsync(alvo)).ShouldHaveSingleItem().Id, Ct);

    protected static AlvoContext Staff(TenantId tenant) => AlvoTestHost.Caller("staff") with { Tenant = tenant };

    protected Dictionary<string, object?> Booking(string note, DateTimeOffset? startsAt = null) => new()
    {
        ["note"] = note,
        ["starts_at"] = startsAt ?? _now.AddDays(1),
        ["tenant_id"] = Tenant.Value,
    };

    protected static Func<AlvoCreated<AlvoRecord>, AlvoEventContext, CancellationToken, Task> ThreeSteps(StepLog log) =>
        async (_, context, ct) =>
        {
            var one = await context.Steps.RunAsync("one", _ => Ran(log, "one", "deposit-1"), ct);
            var two = await context.Steps.RunAsync("two", _ => Ran(log, "two", "email-1"), ct);
            await context.Steps.RunAsync("three", _ => StepThree(log, $"{one}:{two}"), ct);
        };

    private static Func<AlvoCreated<AlvoRecord>, AlvoEventContext, CancellationToken, Task> Twice(StepLog log) =>
        async (_, context, ct) =>
        {
            await context.Steps.RunAsync("one", _ => Ran(log, "one", 1), ct);
            await context.Steps.RunAsync("one", _ => Ran(log, "one", 2), ct);
        };

    private static Func<AlvoCreated<AlvoRecord>, AlvoEventContext, CancellationToken, Task> OwnStep(string mine, StepLog log)
    {
        var failed = 0;

        return async (_, context, ct) =>
        {
            var value = await context.Steps.RunAsync("one", _ => Ran(log, mine, mine), ct);
            log.SeenByThree.Enqueue(value);
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw new InvalidOperationException("fail once after the step");
            }
        };
    }

    protected static Task<T> Ran<T>(StepLog log, string step, T value)
    {
        log.Ran(step);
        return Task.FromResult(value);
    }

    private static Task StepThree(StepLog log, string seen)
    {
        log.Ran("three");
        log.SeenByThree.Enqueue(seen);

        return log.FailsStepThree() ? throw new InvalidOperationException("step three fails") : Task.CompletedTask;
    }

    private static async Task<DeliveryFixture> FixtureAsync(AlvoTestHost alvo, StoredDelivery delivered)
    {
        var payload = (await Store(alvo).FindAsync(delivered.Id, Ct)).ShouldNotBeNull().Payload.ShouldNotBeNull();

        return new DeliveryFixture
        {
            Subscriber = Handler,
            Delivery = delivered.Id,
            Attempt = 1,
            Envelope = JsonDocument.Parse(payload).RootElement.Clone(),
        };
    }

    protected sealed record StepDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
