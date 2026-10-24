using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

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
        await Should.ThrowAsync<OperationCanceledException>(round);
    }

    [Fact]
    public async Task A_cancelled_manual_round_throws_and_leaves_its_claim_to_the_lease()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await FanOutAsync(store, Handler, "tickets:1");
        using var stop = new CancellationTokenSource();

        var round = dispatcher.ManualRoundAsync(stop.Token);
        await UntilAsync(() => store.Rows.Single().Status == "running", "the delivery to be claimed");
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(round);
        store.Rows.Single().Status.ShouldBe("running");
        store.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_manual_round_is_refused_in_the_background_mode()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, mode: AlvoEventDispatchMode.Background);
        await FanOutAsync(store, Handler, "tickets:1");

        await Should.ThrowAsync<InvalidOperationException>(dispatcher.ManualRoundAsync(Ct));

        store.Rows.Single().Status.ShouldBe("pending");
    }

    [Fact]
    public async Task A_manual_round_is_refused_before_the_boot_is_ready()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, ready: false);
        await FanOutAsync(store, Handler, "tickets:1");

        await Should.ThrowAsync<InvalidOperationException>(dispatcher.ManualRoundAsync(Ct));

        store.Rows.Single().Status.ShouldBe("pending");
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
        const string Abandons = "abandons-its-slot";
        const string RunsNext = "runs-in-the-freed-slot";
        using var abandoned = new DeadDeliveries(Abandons, AlvoDeliveryMetrics.AbandonedName);
        var store = new FakeDeliveryStore(TimeProvider.System);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => stuck.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            concurrency: 1,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50),
            id: Abandons,
            alongside: RunsNext);
        await FanOutAsync(store, Abandons, "tickets:1");
        await FanOutAsync(store, RunsNext, "tickets:2");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => store.Rows.Any(row => row.Status == "succeeded"), "the second delivery to run beside the abandoned one");
        stuck.TrySetResult();
        await dispatcher.StopAsync(Ct);

        Statuses(store, Abandons).ShouldBe(["retrying"]);
        Statuses(store, RunsNext).ShouldBe(["succeeded"], "the only slot was free again although the abandoned handler still ran");
        abandoned.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_stands_down_at_its_cap_while_other_handlers_keep_flowing()
    {
        const string Blocks = "ignores-its-token";
        const string Flows = "keeps-flowing";
        using var abandoned = new DeadDeliveries(Blocks, AlvoDeliveryMetrics.AbandonedName);
        var store = new FakeDeliveryStore(TimeProvider.System);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => stuck.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            concurrency: 1,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50),
            id: Blocks,
            alongside: Flows);
        await FanOutAsync(store, Blocks, "tickets:1", "tickets:2", "tickets:3");
        await FanOutAsync(store, Flows, "tickets:4", "tickets:5", "tickets:6");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => Statuses(store, Flows).All(status => status == "succeeded"), "the other handler's deliveries to run");
        await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
        var blocked = Statuses(store, Blocks);
        stuck.TrySetResult();
        await dispatcher.StopAsync(Ct);

        blocked.ShouldBe(["pending", "pending", "retrying"], "with one invocation abandoned and alive, the handler is not claimed for again");
        abandoned.Count.ShouldBe(1);
        _logs.Entries.Count(entry => entry.EventName == "HandlerStoodDown").ShouldBe(1);
        _logs.Entries.Single(entry => entry.EventName == "HandlerStoodDown").Level.ShouldBe(LogLevel.Warning);
    }

    /// <summary>
    /// The handler's timeout, its settle grace and the store's retry delay all run on a <see cref="FakeTimeProvider"/> the fact
    /// moves by hand; only the loop's idle poll runs on the system clock. On the system clock this fact was load-sensitive
    /// in two ways: under a loaded machine the second delivery's thread-pool start alone could outlast the 50 ms timeout, so
    /// it was abandoned too and retried 5 s later; and a run slower than the first delivery's 5 s retry delay ran that
    /// delivery again. Either way the two successes never showed together within the wait budget. Here the clock moves
    /// only until the handler stands down, and then stands still: the later deliveries cannot time out and the retried one
    /// is never due, however slow the machine is.
    /// </summary>
    [Fact]
    public async Task A_stood_down_handler_is_claimed_for_again_once_its_abandoned_invocation_ends()
    {
        const string Blocks = "ignores-its-token";
        var handlerTime = new FakeTimeProvider(_clock.GetUtcNow());
        var store = new FakeDeliveryStore(handlerTime);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => stuck.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            concurrency: 1,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50),
            id: Blocks,
            runnerTime: handlerTime);
        await FanOutAsync(store, Blocks, "tickets:1", "tickets:2", "tickets:3");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => StoodDown(handlerTime), "the handler to stand down");
        stuck.SetResult();
        await UntilAsync(() => Statuses(store, Blocks).Count(status => status == "succeeded") == 2, "the remaining deliveries to run");
        await dispatcher.StopAsync(Ct);

        Statuses(store, Blocks).ShouldBe(["retrying", "succeeded", "succeeded"]);
    }

    [Fact]
    public async Task A_manual_round_does_not_claim_for_a_handler_at_its_cap_of_abandoned_invocations()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => stuck.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            concurrency: 1,
            settle: TimeSpan.FromMilliseconds(50));
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2", "tickets:3");

        var result = await dispatcher.ManualRoundAsync(Ct);
        stuck.SetResult();

        result.Claimed.ShouldBe(1, "the handler's first invocation was abandoned, alive, so it stood down for the rest of the round");
        store.Rows.Count(row => row.Status == "pending").ShouldBe(2);
    }

    [Fact]
    public async Task A_stop_during_a_claim_still_waits_for_the_answer_being_written()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var gated = new AnswerGatedStore(store);
        var dispatcher = Dispatcher(
            store,
            (AlvoCreated<AlvoRecord> _) => Task.CompletedTask,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System,
            port: gated);
        await FanOutAsync(store, Handler, "tickets:1");

        await dispatcher.StartAsync(Ct);
        await gated.Answering.Task.WaitAsync(_waitBudget, Ct);
        await UntilAsync(() => gated.ClaimsWaiting == 1, "the next claim to be in flight");
        var stopping = dispatcher.StopAsync(Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);

        dispatcher.ExecuteTask!.IsCompleted.ShouldBeFalse("the answer being written is awaited before the loop ends");
        gated.Answer.SetResult();
        await stopping;

        store.Rows.Single().Status.ShouldBe("succeeded");
        _logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
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

    [Fact]
    public async Task A_loop_whose_boot_primed_no_catalog_stands_down_and_says_so()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var dispatcher = Dispatcher(
            store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, mode: AlvoEventDispatchMode.Background, time: TimeProvider.System, primed: false);
        await FanOutAsync(store, Handler, "tickets:1");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => dispatcher.ExecuteTask is { IsCompleted: true }, "the loop to stand down");
        await dispatcher.StopAsync(Ct);

        store.Rows.Single().Status.ShouldBe("pending");
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryDispatcherStoodDownUnprimed" && entry.Level == LogLevel.Warning);
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
        IDeliveryStore? port = null,
        string id = Handler,
        bool ready = true,
        string? alongside = null,
        bool primed = true,
        TimeProvider? runnerTime = null)
    {
        var clock = time ?? _clock;
        var deliveries = port ?? store;
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t =>
        {
            t.OnCreated(id, handle, configure: configure);
            if (alongside is not null)
            {
                t.OnCreated(alongside, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
            }
        }));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>());
        var catalogs = new PolicyCatalogProvider();
        if (primed)
        {
            catalogs.SetCurrent("handlers", _catalog);
        }

        var boot = new AlvoBootState();
        if (ready)
        {
            boot.Ready("handlers", appliedRevision: 1);
        }

        var maxConcurrency = Options.Create(new AlvoDeliveryOptions { MaxConcurrency = concurrency });
        var abandoned = new AbandonedHandlers(maxConcurrency, _loggers.CreateLogger<AbandonedHandlers>());
        var runner = new DeliveryRunner(
            deliveries,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            abandoned,
            runnerTime ?? clock,
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
            abandoned,
            Options.Create(new AlvoEventOptions { DispatchMode = mode, PollInterval = TimeSpan.FromMilliseconds(20), BatchSize = batchSize }),
            maxConcurrency,
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

    /// <summary>
    /// Whether the handler has stood down; until it has, moves <paramref name="handlerTime"/> on by the 50 ms timeout and
    /// settle grace, so each poll of the wait fires whichever of the two the runner is waiting on.
    /// </summary>
    private bool StoodDown(FakeTimeProvider handlerTime)
    {
        if (_logs.Entries.Any(entry => entry.EventName == "HandlerStoodDown"))
        {
            return true;
        }

        handlerTime.Advance(TimeSpan.FromMilliseconds(50));
        return false;
    }

    private static List<string> Statuses(FakeDeliveryStore store, string subscriber) =>
        [.. store.Rows.Where(row => row.Subscriber == subscriber).Select(row => row.Status).Order(StringComparer.Ordinal)];

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

    /// <summary>
    /// The in-memory store whose first claim passes, whose answers wait for <see cref="Answer"/>, and whose later claims
    /// wait for the stop — a stop that lands mid-claim while an answer is being written.
    /// </summary>
    private sealed class AnswerGatedStore(FakeDeliveryStore inner) : IDeliveryStore
    {
        private int _claims;
        private int _waiting;

        internal TaskCompletionSource Answering { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ClaimsWaiting => Volatile.Read(ref _waiting);

        public Task EnsureAsync(CancellationToken cancellationToken = default) => inner.EnsureAsync(cancellationToken);

        public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
            inner.FanOutAsync(requests, cancellationToken);

        public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _claims) == 1)
            {
                return await inner.ClaimAsync(claim, cancellationToken);
            }

            Interlocked.Increment(ref _waiting);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public async Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default)
        {
            Answering.TrySetResult();
            await Answer.Task;
            return await inner.CompleteAsync(attempt, cancellationToken);
        }

        public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
            inner.FailAsync(attempt, failure, cancellationToken);

        public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
            inner.PendingSubscribersAsync(cancellationToken);

        public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
            inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken);

        public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
            inner.CancelPendingAsync(subscriber, cancellationToken);
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
