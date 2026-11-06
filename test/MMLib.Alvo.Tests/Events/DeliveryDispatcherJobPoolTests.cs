using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Scheduled jobs run from a pool of their own: a job is claimed while every handler slot is busy, and no more jobs run at
/// once than <see cref="AlvoDeliveryOptions.MaxJobConcurrency"/>.
/// </summary>
public sealed class DeliveryDispatcherJobPoolTests
{
    private const string Handler = "on-created";
    private const string Job = "nightly-roll";
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero));
    private readonly JobRuns _runs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_runs_while_every_handler_slot_is_busy()
    {
        var store = new FakeDeliveryStore(_clock);
        var handlerSawTheJob = false;
        var dispatcher = Dispatcher(
            store,
            async (AlvoCreated<AlvoRecord> _) =>
            {
                handlerSawTheJob = await _runs.FirstRan.Task.WaitAsync(_waitBudget, Ct);
            },
            maxConcurrency: 1,
            maxJobConcurrency: 1);
        await FanOutAsync(store, Handler, "tickets:1");
        await FanOutAsync(store, Job, "schedules:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(2);

        handlerSawTheJob.ShouldBeTrue("the job was claimed beside the one busy handler slot, not after it");
        store.Rows.ShouldAllBe(row => row.Status == "succeeded");
    }

    [Fact]
    public async Task No_more_jobs_are_claimed_at_once_than_the_job_concurrency_and_never_with_the_handlers()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, maxConcurrency: 16, maxJobConcurrency: 1);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2");
        await FanOutAsync(store, Job, "schedules:1", "schedules:2");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(4);

        var claims = store.Claims.ToList();
        var jobClaims = claims.Where(claim => claim.Unordered.Contains(Job)).ToList();
        jobClaims.ShouldAllBe(claim => claim.BatchSize == 1 && claim.Ordered.Count == 0 && claim.Unordered.Count == 1);
        claims.Except(jobClaims).ShouldAllBe(claim => !claim.Unordered.Contains(Job) && !claim.Ordered.Contains(Job));
        jobClaims[0].Lease.ShouldBe(TimeSpan.FromMinutes(15) + DeliveryRunner.LeaseGrace + DeliveryRunner.LeaseGrace, "the job pool's lease is its own timeout's");
        _runs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_manual_round_whose_handlers_would_fill_its_batch_still_runs_the_due_job()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, maxConcurrency: 16, maxJobConcurrency: 2, batchSize: 1);
        await FanOutAsync(store, Handler, "tickets:1");
        await FanOutAsync(store, Job, "schedules:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(1);

        _runs.Count.ShouldBe(1);
        store.Rows.Single(row => row.Subscriber == Handler).Status.ShouldBe("pending");
    }

    [Fact]
    public void An_abandoned_job_stands_down_at_the_job_cap_and_a_handler_at_the_handler_cap()
    {
        var abandoned = new AbandonedHandlers(
            Options.Create(new AlvoDeliveryOptions { MaxConcurrency = 16, MaxJobConcurrency = 2 }), NullLogger<AbandonedHandlers>.Instance);
        var stuck = new TaskCompletionSource();

        abandoned.Track(Job, stuck.Task, job: true);
        abandoned.StoodDown(Job, job: true).ShouldBeFalse();
        abandoned.Track(Job, stuck.Task, job: true);

        abandoned.StoodDown(Job, job: true).ShouldBeTrue("two abandoned runs reach the job pool's cap of 2");
        abandoned.StoodDown(Job, job: false).ShouldBeFalse("the handler cap is 16");
        stuck.SetResult();
        abandoned.StoodDown(Job, job: true).ShouldBeFalse();
    }

    private DeliveryDispatcher Dispatcher(FakeDeliveryStore store, Delegate handle, int maxConcurrency, int maxJobConcurrency, int batchSize = 100)
    {
        var registry = HandlerCatalog.Registry(alvo => alvo
            .Entity("tickets", tickets => tickets.OnCreated(Handler, handle))
            .AddSchedule<RecordingJob>(Job, "0 2 * * *"));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", HandlerCatalog.Catalog());
        var boot = new AlvoBootState();
        boot.Ready("handlers", appliedRevision: 1);
        var deliveries = Options.Create(new AlvoDeliveryOptions { MaxConcurrency = maxConcurrency, MaxJobConcurrency = maxJobConcurrency });
        var abandoned = new AbandonedHandlers(deliveries, NullLogger<AbandonedHandlers>.Instance);
        var runner = new DeliveryRunner(
            store,
            new ServiceCollection().AddSingleton(_runs).AddTransient<RecordingJob>().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            abandoned,
            _clock,
            NullLogger<DeliveryRunner>.Instance)
        {
            SettleGrace = DeliveryRunner.LeaseGrace,
        };

        return new DeliveryDispatcher(
            store,
            boot,
            catalogs,
            binder,
            runner,
            abandoned,
            Options.Create(new AlvoEventOptions { DispatchMode = AlvoEventDispatchMode.Manual, BatchSize = batchSize }),
            deliveries,
            _clock,
            NullLogger<DeliveryDispatcher>.Instance);
    }

    private async Task FanOutAsync(FakeDeliveryStore store, string subscriber, params string[] keys)
    {
        foreach (var key in keys)
        {
            var @event = Event(subscriber, key);
            store.Publish(@event);
            await store.FanOutAsync(
                [new DeliveryRequest { EventId = @event.Id, Subscriber = subscriber, PartitionKey = key, Kind = subscriber == Job ? "schedule" : "handler" }],
                Ct);
        }
    }

    private AlvoEvent Event(string subscriber, string key) => new()
    {
        Id = AlvoEventId.Create(_clock.GetUtcNow()),
        Source = AlvoEvent.DefaultSource,
        Type = subscriber == Job ? "alvo.schedule.nightly_roll" : "entity.tickets.created",
        Time = _clock.GetUtcNow(),
        Subject = key.Replace(':', '/'),
        PartitionKey = key,
        AuthType = AlvoEventAuthType.System,
        CorrelationId = "c",
        Data = new AlvoEventData
        {
            Record = subscriber == Job
                ? new AlvoRecord(new Dictionary<string, object?> { ["schedule"] = Job, ["scheduledFor"] = "2026-11-07T02:00:00.0000000Z" })
                : AlvoRecord.Empty,
        },
    };

    /// <summary>The runs the job pool made, and a signal raised by the first.</summary>
    internal sealed class JobRuns
    {
        private int _count;

        internal TaskCompletionSource<bool> FirstRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Count => Volatile.Read(ref _count);

        internal void Ran()
        {
            Interlocked.Increment(ref _count);
            FirstRan.TrySetResult(true);
        }
    }

    /// <summary>Records each run.</summary>
    internal sealed class RecordingJob(JobRuns runs) : IAlvoScheduledJob
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
        {
            runs.Ran();
            return Task.CompletedTask;
        }
    }
}
