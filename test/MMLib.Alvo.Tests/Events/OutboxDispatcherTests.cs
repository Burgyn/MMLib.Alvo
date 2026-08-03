using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using System.Globalization;
using System.Net;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The pump: gated on the boot state, containing every failure, and ending the moment its token is cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the readiness gate is proven here and not against a running host.</b> <c>AlvoBootService</c> does all
/// of its work in <c>IHostedLifecycleService.StartingAsync</c>, and the host runs every service's
/// <c>StartingAsync</c> before <em>any</em> service's <c>StartAsync</c> — so a host-level fact that holds the
/// boot mid-flight holds the whole <c>StartAsync</c> phase with it, and the dispatcher's <c>ExecuteAsync</c> has
/// not begun at all. Such a fact would pass because the pump never ran, which is exactly the vacuity a
/// background-service test is prone to. Here the pump really is running while the state is
/// <see cref="AlvoBootPhase.Pending"/>, and the same facts also cover what the ordering can never arrange: a
/// boot that <em>refused</em>, an embedded host that primes on its own schedule, and any future change that
/// moves priming out of the startup phase.
/// </para>
/// <para>
/// <b>Time is real and the intervals are tiny.</b> A <c>FakeTimeProvider</c> would be a new test dependency, and
/// PR5a is allowed exactly one (a CloudEvents conformance oracle). Every wait either polls a condition with a
/// deadline that <em>throws</em> — a drain that silently gave up would make every count below an under-count —
/// or is a fixed span of many poll intervals in the facts that assert nothing happened.
/// </para>
/// </remarks>
public sealed class OutboxDispatcherTests : IDisposable
{
    /// <inheritdoc/>
    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    /// <summary>
    /// The gate is on the boot <b>state</b>, not on registration order — and the pump is demonstrably alive
    /// while it waits, because releasing the state is all it takes to make it claim.
    /// </summary>
    /// <remarks>
    /// .NET 10 runs all of <c>ExecuteAsync</c> off the startup thread, so ordering cannot express this and
    /// <c>await Task.Yield()</c> as a first line is dead code. What the gate protects is not tidiness: an
    /// unprimed catalog knows no entity, so every event would match no hook, count as filtered and be
    /// <em>retired</em> — silent, permanent loss that no retry recovers.
    /// </remarks>
    [Fact]
    public async Task The_pump_claims_nothing_before_the_boot_reports_ready()
    {
        var store = StoreWith(WonDeal(), WonDeal(), WonDeal());
        await using var pump = await StartAsync(store);

        await Task.Delay(_manyPollIntervals, Cancellation);
        store.ClaimCount.ShouldBe(0, "the boot has published nothing, so nothing may be claimed");

        pump.Boot.Ready(Project, appliedRevision: 1);

        await UntilAsync(() => store.DispatchedIds.Count == 3, "three events to be dispatched once ready");
    }

    /// <summary>
    /// A boot that refused leaves the pump doing nothing at all, rather than claiming against a catalog that was
    /// never primed.
    /// </summary>
    [Fact]
    public async Task A_boot_that_refused_leaves_the_pump_claiming_nothing()
    {
        var store = StoreWith(WonDeal());
        await using var pump = await StartAsync(store, boot => boot.Failed("stage 0 refused"));

        await Task.Delay(_manyPollIntervals, Cancellation);

        store.ClaimCount.ShouldBe(0);
        store.EnsureCount.ShouldBe(0);
        pump.Service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue(
            "the pump ends rather than parks, so the host's StopAsync has nothing to wait for");
    }

    /// <summary>
    /// The switch stops delivery without touching the queue, which is what a replica that must not dispatch
    /// needs.
    /// </summary>
    [Fact]
    public async Task The_dispatcher_can_be_switched_off_entirely()
    {
        var store = StoreWith(WonDeal());
        await using var pump = await StartAsync(store, Ready, options => options.Enabled = false);

        await Task.Delay(_manyPollIntervals, Cancellation);

        store.EnsureCount.ShouldBe(0);
        store.ClaimCount.ShouldBe(0);
        store.DispatchedIds.ShouldBeEmpty();
    }

    /// <summary>
    /// Every claim carries the configured batch size, ceiling and lease — so the options are what the queue is
    /// asked with, rather than settings nothing reads.
    /// </summary>
    [Fact]
    public async Task A_claim_carries_the_configured_batch_size_ceiling_and_lease()
    {
        var store = StoreWith(WonDeal());
        await using var pump = await StartAsync(store, Ready, options =>
        {
            options.BatchSize = 7;
            options.MaxAttempts = 4;
            options.ClaimLease = TimeSpan.FromMinutes(3);
        });

        await UntilAsync(() => store.Claims.Count > 0, "one claim");

        var claim = store.Claims[0];
        claim.BatchSize.ShouldBe(7);
        claim.MaxAttempts.ShouldBe(4);
        claim.Lease.ShouldBe(TimeSpan.FromMinutes(3));
        claim.Claimant.ShouldContain(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Deviation 71: one poison event must not stop the pump, and must never escape <c>ExecuteAsync</c>.
    /// </summary>
    /// <remarks>
    /// <c>HostOptions.BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c>, and from .NET 11
    /// <c>RunAsync</c>/<c>StopAsync</c> also throw and the process exits non-zero — with the documented
    /// recommended action being "do nothing", because a failing app should fail. So the containment belongs
    /// inside the loop, per entry, never at the host's edge.
    /// </remarks>
    [Fact]
    public async Task A_delivery_that_always_throws_is_retried_to_the_ceiling_and_then_left_alone()
    {
        var store = StoreWith(WonDeal());
        await using var pump = await StartAsync(
            store, Ready, options => options.MaxAttempts = 3, HttpStatusCode.ServiceUnavailable);

        await UntilAsync(() => Errors.Count == 1, "the poison event to be given up on");
        await Task.Delay(_manyPollIntervals, Cancellation);

        store.OnlyEntry.Attempts.ShouldBe(3, "the ceiling is the only bound, and it is reached exactly once");
        store.OnlyEntry.Dispatched.ShouldBeFalse("an abandoned event stays in the queue with dispatched_at unset");
        store.ReleasedIds.Count.ShouldBe(3);
        _logs.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(3, "one warning per attempt");
        Errors.ShouldHaveSingleItem().Message.ShouldContain("gave up");
        pump.Service.ExecuteTask!.IsCompleted.ShouldBeFalse("the pump keeps running for every other event");
    }

    /// <summary>
    /// The poison event stops occupying the pump once it hits the ceiling, so events queued behind it are still
    /// delivered. PR5a's stand-in for a dead-letter queue is an attempt ceiling plus a loud log (7.1 owns the
    /// queue), and this is what makes the stand-in adequate rather than merely present.
    /// </summary>
    [Fact]
    public async Task A_poison_event_does_not_block_the_events_queued_behind_it()
    {
        var poison = WonDeal();
        var store = StoreWith(poison, WonDeal(), WonDeal());
        await using var pump = await StartAsync(
            store, Ready, options => options.MaxAttempts = 2, refuseBodiesNaming: poison.Id);

        await UntilAsync(() => store.DispatchedIds.Count == 2, "the two healthy events to be delivered");

        store.EntryOf(poison.Id).Dispatched.ShouldBeFalse();
        store.DispatchedIds.ShouldNotContain(poison.Id);
    }

    /// <summary>
    /// A shutdown ends the pump in milliseconds, not at the host's 30-second <c>ShutdownTimeout</c>.
    /// </summary>
    /// <remarks>
    /// The poll interval is deliberately far longer than the assertion's budget: the pump spends almost all of
    /// its life inside the idle wait, so a wait that ignored its token would keep the host's
    /// <c>StopAsync</c> blocked for the whole interval. That is the measurement — a version of this fact with a
    /// short poll interval would pass on a pump that ignores cancellation entirely.
    /// </remarks>
    [Fact]
    public async Task A_shutdown_returns_promptly_rather_than_waiting_out_the_poll_interval()
    {
        var store = new FakeOutboxStore();
        await using var pump = await StartAsync(
            store, Ready, options => options.PollInterval = TimeSpan.FromSeconds(30));
        await UntilAsync(() => store.ClaimCount > 0, "the pump to reach its idle wait");

        var elapsed = await pump.MeasureStopAsync();

        elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(5),
            "the host blocks in StopAsync waiting for ExecuteAsync, with a 30 s ShutdownTimeout, so the loop "
            + "must observe its cancellation token promptly");
        pump.Service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue("a shutdown is not a failure");
    }

    /// <summary>
    /// An entry in flight when the shutdown arrives is left <b>claimed</b>, not released and not reported as a
    /// failed attempt: its lease is what recovers it, which is the same mechanism that covers a process that
    /// died.
    /// </summary>
    /// <remarks>
    /// This is the fact that keeps the containment from swallowing cancellation. A catch without the
    /// <c>stoppingToken</c> filter would treat every shutdown as a delivery failure — one spurious warning and
    /// one spurious attempt per in-flight entry on every deploy.
    /// </remarks>
    [Fact]
    public async Task An_entry_in_flight_when_the_host_stops_is_left_claimed_rather_than_failed()
    {
        var store = StoreWith(WonDeal());
        var blocked = new TaskCompletionSource();
        await using var pump = await StartAsync(store, Ready, blockDeliveriesOn: blocked);
        await UntilAsync(() => store.ClaimCount > 0, "the delivery to be in flight");

        await pump.MeasureStopAsync();

        store.ReleasedIds.ShouldBeEmpty("a shutdown is not a failed attempt, so nothing is handed back");
        _logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
        store.OnlyEntry.Dispatched.ShouldBeFalse();
    }

    /// <summary>
    /// An event that matched no hook is retired, not retried — and writes no execution-log entry, which is the
    /// half of that criterion no counter can express.
    /// </summary>
    [Fact]
    public async Task An_event_that_matches_no_hook_is_retired_rather_than_retried()
    {
        var store = StoreWith(Event("entity.vehicles.created"));
        var pump = Subject(store, Ready);

        (await pump.PumpOneBatchAsync(Cancellation)).ShouldBe(1);

        store.DispatchedIds.ShouldHaveSingleItem();
        store.ReleasedIds.ShouldBeEmpty();
        _logs.Entries.ShouldBeEmpty("a filtered event costs one counter increment and no log entry at all");
    }

    /// <summary>
    /// A batch that reaches an unprimed catalog retires <b>nothing</b>: the loud version costs a stopped pump and
    /// keeps every event, the quiet version would count them all as unmatched and retire them.
    /// </summary>
    [Fact]
    public async Task A_batch_with_no_primed_catalog_retires_nothing()
    {
        var store = StoreWith(WonDeal());
        var pump = Subject(store, Ready, catalogs: new PolicyCatalogProvider());

        await pump.PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldBeEmpty(
            "an event judged against an unprimed catalog matches no hook, and a matchless event is retired — "
            + "so treating this as 'nothing subscribed' would lose every event in the batch permanently");
        store.ReleasedIds.ShouldHaveSingleItem();
        _logs.Entries.ShouldHaveSingleItem()
            .Exception.ShouldNotBeNull().Message.ShouldContain("primed policy catalog");
    }

    /// <summary>
    /// A failure that is nobody's event in particular — the queue itself being unreachable — is contained too:
    /// the pump stops with one Error line and <c>ExecuteAsync</c> completes, so the host keeps serving.
    /// </summary>
    /// <remarks>
    /// Nothing restarts the pump, and that is the honest answer without a supervision policy: the Error line is
    /// the notification, and its wording says the process serves requests and delivers no events until it is
    /// restarted.
    /// </remarks>
    [Fact]
    public async Task A_queue_that_cannot_be_reached_stops_the_pump_loudly_without_faulting_the_host()
    {
        var store = new FakeOutboxStore { ClaimThrows = new InvalidOperationException("the database is gone") };
        await using var pump = await StartAsync(store, Ready);

        await UntilAsync(() => pump.Service.ExecuteTask!.IsCompleted, "the pump to end");

        pump.Service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue(
            "an escaping failure would take down a host serving HTTP, because "
            + "BackgroundServiceExceptionBehavior defaults to StopHost");
        Errors.ShouldHaveSingleItem().Message.ShouldContain("stopped");
        Errors[0].Exception.ShouldNotBeNull().Message.ShouldBe("the database is gone");
    }

    private const string Project = "test";
    private const string EndpointName = "crm-sync";
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan _manyPollIntervals = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(10);

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;

    /// <summary>Builds the one logger pipeline every fact reads, so the facts run through the real delegates.</summary>
    public OutboxDispatcherTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IReadOnlyList<CapturedLogEntry> Errors =>
        [.. _logs.Entries.Where(entry => entry.Level == LogLevel.Error)];

    private static void Ready(AlvoBootState boot) => boot.Ready(Project, appliedRevision: 1);

    /// <summary>Starts one dispatcher as the host would, and hands back everything a fact reads off it.</summary>
    private async Task<RunningPump> StartAsync(
        FakeOutboxStore store,
        Action<AlvoBootState>? publish = null,
        Action<AlvoEventOptions>? configure = null,
        HttpStatusCode status = HttpStatusCode.OK,
        Guid? refuseBodiesNaming = null,
        TaskCompletionSource? blockDeliveriesOn = null)
    {
        var boot = new AlvoBootState();
        publish?.Invoke(boot);

        var service = Subject(store, publish: null, configure, status, refuseBodiesNaming, blockDeliveriesOn, boot: boot);
        await service.StartAsync(Cancellation);

        return new RunningPump(service, boot);
    }

    /// <summary>Builds one dispatcher over the real catalog, evaluator, executor and delivery.</summary>
    private OutboxDispatcher Subject(
        FakeOutboxStore store,
        Action<AlvoBootState>? publish = null,
        Action<AlvoEventOptions>? configure = null,
        HttpStatusCode status = HttpStatusCode.OK,
        Guid? refuseBodiesNaming = null,
        TaskCompletionSource? blockDeliveriesOn = null,
        IPolicyCatalogProvider? catalogs = null,
        AlvoBootState? boot = null)
    {
        var options = new AlvoEventOptions { PollInterval = _pollInterval };
        configure?.Invoke(options);

        boot ??= new AlvoBootState();
        publish?.Invoke(boot);

        return new OutboxDispatcher(
            store,
            boot,
            catalogs ?? PrimedCatalogs,
            CelFixtures.Evaluator,
            new EventActionExecutor(
                new WebhookDelivery(new StubHttpClientFactory(
                    new StubWebhookReceiver(status, refuseBodiesNaming, blockDeliveriesOn))),
                new DiscardingEmailSender(),
                _loggers.CreateLogger<EventActionExecutor>()),
            Options.Create(options),
            TimeProvider.System,
            _loggers.CreateLogger<OutboxDispatcher>());
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, and throws rather than giving up quietly.</summary>
    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + _waitBudget;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {_waitBudget} for {what}, and it never happened.");
            }

            await Task.Delay(_pollDelay, Cancellation);
        }
    }

    private static readonly TimeSpan _pollDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>One running dispatcher, stopped and disposed with the fact.</summary>
    private sealed class RunningPump(OutboxDispatcher service, AlvoBootState boot) : IAsyncDisposable
    {
        internal OutboxDispatcher Service => service;

        internal AlvoBootState Boot => boot;

        /// <summary>How long a stop took — the host blocks in <c>StopAsync</c> for exactly this long.</summary>
        internal async Task<TimeSpan> MeasureStopAsync()
        {
            var started = DateTimeOffset.UtcNow;
            await service.StopAsync(CancellationToken.None);

            return DateTimeOffset.UtcNow - started;
        }

        public async ValueTask DisposeAsync()
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "deals",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = "stage", Type = FieldType.Enum, EnumValues = ["lead", "won", "lost"] },
            ],
        },
        new EntitySchema
        {
            Name = "vehicles",
            Tenancy = TenancyMode.Global,
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }],
        },
    ]);

    private static AlvoDescriptor Descriptor { get; } = new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = Project,
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            ["deals"] = new()
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Hooks = new EntityHooks
                {
                    AfterUpdate =
                    [
                        new AfterHook
                        {
                            Condition = "new.stage == 'won'",
                            Action = new WebhookAction { Endpoint = EndpointName },
                        },
                    ],
                },
            },
            ["vehicles"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                [EndpointName] = new() { Url = "https://example.test/hook", SecretRef = "crm-sync-secret" },
            },
        },
    };

    private static PolicyCatalog PrimedCatalogsSource { get; } = Catalog();

    private static IPolicyCatalogProvider PrimedCatalogs
    {
        get
        {
            var provider = new PolicyCatalogProvider();
            provider.SetCurrent(Project, PrimedCatalogsSource);

            return provider;
        }
    }

    private static PolicyCatalog Catalog()
    {
        PolicyCatalog.TryBuild(Descriptor, Schema, CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue($"expected a clean build, got: {string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"))}");

        return catalog!;
    }

    private static FakeOutboxStore StoreWith(params AlvoEvent[] events)
    {
        var store = new FakeOutboxStore();
        foreach (var @event in events)
        {
            store.Append(@event);
        }

        return store;
    }

    /// <summary>An event whose hook condition holds, so it is delivered rather than filtered.</summary>
    private static AlvoEvent WonDeal() =>
        Event("entity.deals.updated", Record(("stage", "won")), Record(("stage", "lead")));

    private static AlvoEvent Event(string type, AlvoRecord? record = null, AlvoRecord? oldRecord = null) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero),
        Subject = "deals/019000aa-0000-7000-8000-0000000000ff",
        PartitionKey = "deals:019000aa-0000-7000-8000-0000000000ff",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "019000aa-0000-7000-8000-0000000000c0",
        Data = new AlvoEventData { Record = record ?? AlvoRecord.Empty, OldRecord = oldRecord, Changed = ["stage"] },
    };

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));

    /// <summary>
    /// The queue, in memory, with the two behaviours the real one is held to: a claim counts the attempt, and a
    /// release does <b>not</b> roll it back.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than substituted because every fact here is about a sequence of calls over shared
    /// state — how many attempts an entry accumulated, what was retired, what was handed back — which a
    /// call-recording mock cannot answer without re-implementing exactly this.
    /// </remarks>
    private sealed class FakeOutboxStore : IOutboxStore
    {
        private readonly List<FakeEntry> _entries = [];
        private readonly List<ClaimRequest> _claims = [];
        private readonly List<Guid> _dispatched = [];
        private readonly List<Guid> _released = [];
        private readonly Lock _gate = new();
        private int _ensureCount;

        /// <summary>What <see cref="ClaimAsync"/> throws instead of answering, when it throws.</summary>
        internal Exception? ClaimThrows { get; init; }

        internal int EnsureCount => Volatile.Read(ref _ensureCount);

        internal int ClaimCount => Claims.Count;

        internal IReadOnlyList<ClaimRequest> Claims => Snapshot(_claims);

        internal IReadOnlyList<Guid> DispatchedIds => Snapshot(_dispatched);

        internal IReadOnlyList<Guid> ReleasedIds => Snapshot(_released);

        /// <summary>The only entry, for the facts that queue exactly one.</summary>
        internal FakeEntry OnlyEntry
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count == 1
                        ? _entries[0]
                        : throw new InvalidOperationException($"{_entries.Count} entries, not one.");
                }
            }
        }

        internal FakeEntry EntryOf(Guid id)
        {
            lock (_gate)
            {
                return _entries.Single(entry => entry.Id == id);
            }
        }

        internal void Append(AlvoEvent @event)
        {
            lock (_gate)
            {
                _entries.Add(new FakeEntry(@event.Id, @event.Type, AlvoEventJson.Write(@event)));
            }
        }

        public Task EnsureAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _ensureCount);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxEntry>> ClaimAsync(
            string claimant,
            int batchSize,
            int maxAttempts,
            TimeSpan lease,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _claims.Add(new ClaimRequest(claimant, batchSize, maxAttempts, lease));
            }

            return ClaimThrows is not null
                ? Task.FromException<IReadOnlyList<OutboxEntry>>(ClaimThrows)
                : Task.FromResult(Claimed(claimant, batchSize, maxAttempts));
        }

        public Task MarkDispatchedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var entry = _entries.Single(candidate => candidate.Id == id);
                entry.Dispatched = true;
                entry.Claimed = false;
                _dispatched.Add(id);
            }

            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _entries.Single(candidate => candidate.Id == id).Claimed = false;
                _released.Add(id);
            }

            return Task.CompletedTask;
        }

        private IReadOnlyList<OutboxEntry> Claimed(string claimant, int batchSize, int maxAttempts)
        {
            lock (_gate)
            {
                var claimable = _entries
                    .Where(entry => !entry.Dispatched && !entry.Claimed && entry.Attempts < maxAttempts)
                    .Take(batchSize)
                    .ToList();

                return [.. claimable.Select(entry => Claim(entry, claimant))];
            }
        }

        private static OutboxEntry Claim(FakeEntry entry, string claimant)
        {
            entry.Claimed = true;
            entry.ClaimedBy = claimant;
            entry.Attempts++;

            return new OutboxEntry(entry.Id, entry.Type, "deals:1", entry.Payload, entry.Attempts);
        }

        private IReadOnlyList<T> Snapshot<T>(List<T> values)
        {
            lock (_gate)
            {
                return [.. values];
            }
        }
    }

    /// <summary>One queued entry's mutable state, exactly the columns a fact reads.</summary>
    private sealed class FakeEntry(Guid id, string type, string payload)
    {
        internal Guid Id => id;

        internal string Type => type;

        internal string Payload => payload;

        internal int Attempts { get; set; }

        internal bool Claimed { get; set; }

        internal string? ClaimedBy { get; set; }

        internal bool Dispatched { get; set; }
    }

    /// <summary>One claim, as the dispatcher asked for it.</summary>
    private sealed record ClaimRequest(string Claimant, int BatchSize, int MaxAttempts, TimeSpan Lease);

    /// <summary>
    /// The receiver every delivery goes to: answers <paramref name="status"/>, refuses a body naming
    /// <paramref name="refuseBodiesNaming"/>, and can be held open so a fact can stop the host mid-delivery.
    /// </summary>
    private sealed class StubWebhookReceiver(
        HttpStatusCode status, Guid? refuseBodiesNaming, TaskCompletionSource? blockOn) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (blockOn is not null)
            {
                await blockOn.Task.WaitAsync(cancellationToken);
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(Refuses(body) ? HttpStatusCode.ServiceUnavailable : status);
        }

        private bool Refuses(string body) =>
            refuseBodiesNaming is { } poison
            && body.Contains(poison.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An <see cref="IHttpClientFactory"/> over one handler.</summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A mail port that succeeds and keeps nothing: no fact here is about email.</summary>
    private sealed class DiscardingEmailSender : IEmailSender
    {
        public Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
