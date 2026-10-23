using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The delivery dispatcher: which deliveries a claim takes, how many run at once, and how a round reports.</summary>
public sealed class DeliveryDispatcherTests : IDisposable
{
    private const string Handler = "on-created";

    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(10);

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

    public DeliveryDispatcherTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manual_round_runs_every_due_lane_head_and_reports_it()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2");

        var result = await dispatcher.ManualRoundAsync(Ct);

        result.Claimed.ShouldBe(2);
        result.Failed.ShouldBe(0);
        result.Subscribers.ShouldBe([Handler]);
        store.Rows.ShouldAllBe(row => row.Status == "succeeded");
    }

    [Fact]
    public async Task A_round_leaves_the_work_of_a_handler_this_host_does_not_register()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, "someone-else", "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_attempt_is_counted_once()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()));
        await FanOutAsync(store, Handler, "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Failed.ShouldBe(1);
    }

    [Fact]
    public async Task An_unordered_handler_is_not_held_back_by_a_dead_earlier_delivery()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, options => options.Ordering = AlvoOrdering.None);
        var dead = await PublishAsync(store, "tickets:1");
        await store.FanOutAsync([Request(dead, Handler, "tickets:1") with { DeadReason = "when-failed" }], Ct);
        await FanOutAsync(store, Handler, "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(1);
    }

    [Fact]
    public async Task A_claim_holds_for_the_longest_handler_timeout_its_grace_and_its_settle_margin()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), options => options.Timeout = TimeSpan.FromSeconds(20));
        await FanOutAsync(store, Handler, "tickets:1");
        using var stop = new CancellationTokenSource();

        var round = dispatcher.ManualRoundAsync(stop.Token);
        await UntilAsync(() => store.Rows.Single().Status == "running", "the delivery to be claimed");

        store.Rows.Single().LeaseUntil.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(80), "20 s timeout + 30 s grace + 30 s settle margin");
        await stop.CancelAsync();
        await round;
    }

    [Fact]
    public async Task A_manual_round_claims_no_more_deliveries_at_once_than_its_concurrency()
    {
        var store = new FakeDeliveryStore(_clock);
        var most = 0;
        var dispatcher = Dispatcher(
            store,
            async (AlvoCreated<AlvoRecord> _) =>
            {
                InterlockedMax(ref most, store.Rows.Count(row => row.Status == "running"));
                await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            },
            concurrency: 2);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2", "tickets:3", "tickets:4", "tickets:5");

        var result = await dispatcher.ManualRoundAsync(Ct);

        result.Claimed.ShouldBe(5);
        store.Rows.ShouldAllBe(row => row.Status == "succeeded");
        Volatile.Read(ref most).ShouldBe(2, "a delivery claimed before a slot is free would wait with its lease running");
    }

    [Fact]
    public async Task A_manual_round_stops_once_it_has_claimed_the_batch_size()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, concurrency: 2, batchSize: 3);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2", "tickets:3", "tickets:4", "tickets:5");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(3);

        store.Rows.Count(row => row.Status == "pending").ShouldBe(2);
    }

    [Fact]
    public async Task A_later_event_of_a_lane_whose_head_finished_runs_in_the_same_round()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(2);

        store.Rows.ShouldAllBe(row => row.Status == "succeeded");
    }

    [Fact]
    public async Task The_background_loop_runs_no_more_deliveries_at_once_than_its_concurrency()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        var dispatcher = Dispatcher(
            store,
            async (AlvoCreated<AlvoRecord> _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                await gate.Task;
                Interlocked.Decrement(ref running);
            },
            concurrency: 2,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2", "tickets:3", "tickets:4", "tickets:5");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => Volatile.Read(ref running) == 2, "two deliveries to be running");
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        gate.SetResult();
        await UntilAsync(() => store.Rows.All(row => row.Status == "succeeded"), "every delivery to finish");
        await dispatcher.StopAsync(Ct);

        Volatile.Read(ref most).ShouldBe(2);
    }

    [Fact]
    public async Task An_abandoned_handler_does_not_hold_a_slot_of_the_background_loop()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => Interlocked.Increment(ref calls) == 1 ? stuck.Task : Task.CompletedTask,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            concurrency: 1,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => store.Rows.Any(row => row.Status == "succeeded"), "the second delivery to run beside the abandoned one");
        stuck.TrySetResult();
        await dispatcher.StopAsync(Ct);

        store.Rows.Select(row => row.Status).Order(StringComparer.Ordinal).ShouldBe(["retrying", "succeeded"]);
    }

    [Fact]
    public async Task The_background_loop_survives_a_round_whose_claim_fails()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var flaky = new ClaimFailingStore(store, failures: 2);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => Task.CompletedTask,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            port: flaky);
        await FanOutAsync(store, Handler, "tickets:1");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => store.Rows.Single().Status == "succeeded", "the delivery to run once the claim recovers");
        await dispatcher.StopAsync(Ct);

        var failed = _logs.Entries.Where(entry => entry.EventName == "DeliveryDispatcherRoundFailed").ToList();
        failed.Count.ShouldBe(2);
        failed.ShouldAllBe(entry => entry.Level == LogLevel.Error && entry.Exception == null);
        failed.ShouldAllBe(entry => entry.Message.Contains(typeof(TimeoutException).FullName!) && !entry.Message.Contains(ClaimFailingStore.Secret));
        _logs.Entries.ShouldNotContain(entry => entry.EventName == "DeliveryDispatcherStopped");
    }

    [Fact]
    public async Task In_the_manual_mode_the_loop_stands_down_and_says_so()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, Handler, "tickets:1");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => dispatcher.ExecuteTask is { IsCompleted: true }, "the loop to stand down");
        await dispatcher.StopAsync(Ct);

        dispatcher.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        store.Rows.Single().Status.ShouldBe("pending");
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryDispatcherManual");
    }

    private DeliveryDispatcher Dispatcher(
        FakeDeliveryStore store,
        Delegate handle,
        Action<AlvoHandlerOptions>? configure = null,
        int concurrency = 16,
        AlvoEventDispatchMode mode = AlvoEventDispatchMode.Manual,
        TimeProvider? time = null,
        int batchSize = 100,
        TimeSpan? settle = null,
        IDeliveryStore? port = null)
    {
        var clock = time ?? _clock;
        var deliveries = port ?? store;
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.OnCreated(Handler, handle, configure: configure)));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>());
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", _catalog);
        var boot = new AlvoBootState();
        boot.Ready("handlers", appliedRevision: 1);
        var runner = new DeliveryRunner(
            deliveries,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            clock,
            _loggers.CreateLogger<DeliveryRunner>())
        {
            SettleGrace = settle ?? DeliveryRunner.LeaseGrace,
        };

        return new DeliveryDispatcher(
            deliveries,
            boot,
            catalogs,
            binder,
            runner,
            Options.Create(new AlvoEventOptions { DispatchMode = mode, PollInterval = TimeSpan.FromMilliseconds(20), BatchSize = batchSize }),
            Options.Create(new AlvoDeliveryOptions { MaxConcurrency = concurrency }),
            clock,
            _loggers.CreateLogger<DeliveryDispatcher>());
    }

    private async Task FanOutAsync(FakeDeliveryStore store, string subscriber, params string[] keys)
    {
        foreach (var key in keys)
        {
            var @event = await PublishAsync(store, key);
            await store.FanOutAsync([Request(@event, subscriber, key)], Ct);
        }
    }

    private static DeliveryRequest Request(Guid @event, string subscriber, string key) =>
        new() { EventId = @event, Subscriber = subscriber, PartitionKey = key, Kind = DeliveryKind.Handler };

    private Task<Guid> PublishAsync(FakeDeliveryStore store, string key)
    {
        var @event = new AlvoEvent
        {
            Id = AlvoEventId.Create(_clock.GetUtcNow()),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = _clock.GetUtcNow(),
            Subject = key.Replace(':', '/'),
            PartitionKey = key,
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };
        store.Publish(@event);

        return Task.FromResult(@event.Id);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
            seen = Volatile.Read(ref target);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + _waitBudget;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {_waitBudget} for {what}, and it never happened.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }

    /// <summary>The in-memory store whose first claims fail the way a database under a deadlock or a restart refuses one.</summary>
    private sealed class ClaimFailingStore(FakeDeliveryStore inner, int failures) : IDeliveryStore
    {
        internal const string Secret = "Password=hunter2";

        private int _failures = failures;

        public Task EnsureAsync(CancellationToken cancellationToken = default) => inner.EnsureAsync(cancellationToken);

        public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
            inner.FanOutAsync(requests, cancellationToken);

        public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default) =>
            Interlocked.Decrement(ref _failures) >= 0
                ? Task.FromException<IReadOnlyList<ClaimedDelivery>>(new TimeoutException($"The claim deadlocked; {Secret}."))
                : inner.ClaimAsync(claim, cancellationToken);

        public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(attempt, cancellationToken);

        public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
            inner.FailAsync(attempt, failure, cancellationToken);

        public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
            inner.PendingSubscribersAsync(cancellationToken);

        public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
            inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken);

        public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
            inner.CancelPendingAsync(subscriber, cancellationToken);
    }
}
