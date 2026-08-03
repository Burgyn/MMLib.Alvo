using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Events;

using System.Globalization;

using Xunit;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// <c>baas-analyza.md:676</c>'s first number: a <b>10 000-event chaos run loses no event</b>, end to end over the
/// real outbox and the real dispatcher, on both shipped engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Modelled on <c>PagingPerformanceTests</c>, which is this repository's own answer to how a numeric criterion
/// is written so it cannot pass vacuously</b>: the setup is asserted <em>before</em> anything is measured, the
/// measured numbers are in the failure message rather than only in a verdict, and one line per run is appended to
/// <c>artifacts/criteria/</c> so a regression is readable as a number instead of surfacing as a timeout.
/// </para>
/// <para>
/// <b>Three things make this a fact rather than a loop that terminates.</b> The queued count is asserted before
/// the drain, so a run over an empty outbox cannot pass. Delivery is verified as a <em>set of event ids</em> taken
/// out of the delivered bodies, so ten thousand redeliveries of one event cannot pass as ten thousand events. And
/// one delivery in <see cref="FailEvery"/> is refused while two claims are abandoned outright, so what is
/// measured is the claim / release / re-claim path and the lease that recovers an orphaned claim — not the happy
/// path with a bigger number on it.
/// </para>
/// <para>
/// <b>The subclass supplies a started database and nothing else</b>, the arrangement
/// <see cref="AlvoEventCriteriaTests"/> uses, so the criterion cannot be weakened per engine. Both legs belong to
/// <see cref="DispatchedEventCollection"/>, because the event counters are process-wide and this suite asserts
/// them by value.
/// </para>
/// </remarks>
public abstract class OutboxChaosCriteriaTests
{
    /// <summary>
    /// Starts one engine's database over <see cref="Project"/>, applying <paramref name="install"/> to the
    /// container after <c>AddAlvo</c> and before it is built.
    /// </summary>
    /// <param name="install">The receiver and the clock the run needs; nothing else is substituted.</param>
    protected abstract Task<IServiceProvider> StartDatabaseAsync(Action<IServiceCollection> install);

    /// <summary>The engine and build configuration a reported number was measured under.</summary>
    protected abstract string EngineDescription { get; }

    /// <summary>
    /// <c>baas-analyza.md:676</c>: <b>10 000 events, and not one of them is lost</b> — every queued event is
    /// delivered, every refused delivery is retried until it lands, every entry an abandoned claim took is
    /// recovered through its lease, and the queue ends empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Lost" is measured three independent ways, because each one alone has a hole.</b> The delivered set
    /// answers "did it arrive"; the outbox tally answers "was it retired rather than left behind, and did the
    /// events exist at all"; the counters answer "did the dispatcher believe it delivered exactly these". A
    /// filtered event is the loss this build could produce silently — it is retired without delivery and never
    /// retried — so <c>alvo.events.filtered</c> is asserted to be <b>zero</b> beside the rest.
    /// </para>
    /// <para>
    /// At-least-once delivery is what is claimed and what is asserted: a refused attempt is redelivered, so the
    /// attempt count is higher than the event count by design. That the <em>accepted</em> count comes out exactly
    /// equal to the event count is a stronger property than the guarantee, and it is pinned anyway — a higher
    /// number here would mean an entry was claimed twice while a delivery was in flight, which is the failure
    /// mode the claim's outer predicate exists to prevent (spike Q4).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Ten_thousand_events_are_all_delivered_and_none_is_lost()
    {
        EventCount.ShouldBe(
            10_000,
            "the criterion is ten thousand events. Every assertion below is written in terms of this constant, so "
            + "lowering it lowers them with it — measured, and the reason this line exists: a run seeded with ten "
            + "events satisfies the queued-count check, the delivered set and the tally, and its published line "
            + "still reads as this criterion. A smaller N is a decision, and it has to be made here.");

        await using var world = await OutboxChaosWorld.StartAsync(Project, FailEvery, StartDatabaseAsync);
        var seed = await SeedTheQueueAsync(world);

        var run = await world.RunWithChaosAsync(AbandonedClaims, EventCount);

        await ReportAsync(
            $"§3 outbox chaos ({EventCount} events, fail 1-in-{FailEvery}, {AbandonedClaims} abandoned claims, "
            + $"{EngineDescription}): {run.Describe()}; seeded in {seed.Elapsed.TotalSeconds:F1}s");

        AssertNothingIsLost(seed, run);
        AssertTheChaosHappened(run);
        AssertTheCountersAgree(run, world.Metrics);
    }

    /// <summary>
    /// Queues the events and proves — before anything is dispatched — that they are there and that one of them is
    /// claimable and readable.
    /// </summary>
    /// <remarks>
    /// The whole criterion rests on this: "no event was lost" is trivially true of a queue nothing ever wrote to,
    /// and of one whose rows the dispatch path cannot read. Both are ruled out here rather than assumed.
    /// </remarks>
    private static async Task<ChaosSeed> SeedTheQueueAsync(OutboxChaosWorld world)
    {
        var seed = await world.SeedAsync(EventCount, RecordFor);
        seed.Ids.Distinct().Count().ShouldBe(
            EventCount, "the seed must mint one id per event, or the set comparisons below compare fewer");
        (await world.TallyAsync()).ShouldBe(
            new AlvoOutboxTally(Pending: EventCount, Retired: 0),
            $"the criterion is about {EventCount} queued events, and 'nothing was lost' is trivially true of a "
            + "queue nothing ever wrote to");

        var queued = await world.ClaimOneAndReleaseAsync();
        queued.ShouldNotBeNull(
            "one seeded entry must be claimable before the run starts, or the seed wrote rows no claim can take");
        queued.Data.Record!.Values[StatusField].ShouldBe(
            QueuedStatus,
            "the claimed entry's stored envelope must deserialize and carry the field the hook's condition reads, "
            + "or the seed stored a payload the dispatch path cannot act on");

        return seed;
    }

    /// <summary>The criterion itself: every queued event arrived, and the queue is empty behind it.</summary>
    private static void AssertNothingIsLost(ChaosSeed seed, ChaosRun run)
    {
        run.HitClaimCap.ShouldBeFalse(
            $"the pump ran into its own claim bound instead of draining the queue; {run.Describe()}");

        var lost = seed.Ids.Count(id => !run.AcceptedIds.Contains(id));
        lost.ShouldBe(
            0,
            $"every one of the {EventCount} queued events must be delivered at least once; {lost} never were. "
            + run.Describe());
        run.AcceptedIds.Count.ShouldBe(
            EventCount,
            $"the delivered set must be exactly the {EventCount} seeded events — an extra id means a delivery "
            + $"carried an envelope the seed never wrote. {run.Describe()}");
        run.Tally.ShouldBe(
            new AlvoOutboxTally(Pending: 0, Retired: EventCount),
            "every entry must be retired and none left pending, or the run delivered events it did not finish. "
            + run.Describe());
    }

    /// <summary>
    /// That the chaos was real: refusals happened, every refused event still arrived, and an orphaned claim was
    /// recovered.
    /// </summary>
    /// <remarks>
    /// Chaos that is configured but never happens is the static-table version of this fact — the version a happy
    /// path passes. Each half is asserted with its own number, so a run that stopped failing deliveries and a run
    /// whose failures were never recovered are two different failures.
    /// </remarks>
    private static void AssertTheChaosHappened(ChaosRun run)
    {
        run.Refused.ShouldBeGreaterThan(
            EventCount / (FailEvery * 2),
            $"the chaos must really have happened rather than been configured; {run.Describe()}");

        var refusedAndNeverAccepted = run.RefusedIds.Count(id => !run.AcceptedIds.Contains(id));
        refusedAndNeverAccepted.ShouldBe(
            0,
            "a refused delivery is released and delivered again — that release is the whole of at-least-once; "
            + $"{refusedAndNeverAccepted} refused events never arrived. {run.Describe()}");

        run.AbandonedClaims.ShouldBe(
            AbandonedClaims, $"a claim must have been abandoned mid-run; {run.Describe()}");
        run.AbandonedIds.ShouldNotBeEmpty(
            "an abandoned claim that took no entry recovers nothing, so the lease was never exercised. "
            + run.Describe());

        var abandonedAndNeverAccepted = run.AbandonedIds.Count(id => !run.AcceptedIds.Contains(id));
        abandonedAndNeverAccepted.ShouldBe(
            0,
            "an entry whose claimant never came back is recovered by its lease and by nothing else; "
            + $"{abandonedAndNeverAccepted} of {run.AbandonedIds.Count} such entries never arrived. "
            + run.Describe());
    }

    /// <summary>
    /// That the dispatcher's own account of the run matches the receiver's, counter by counter.
    /// </summary>
    /// <remarks>
    /// The <c>filtered</c> counter is the one that matters most here and it is asserted to be <b>zero</b>: every
    /// seeded event matches the one declared hook, and a filtered event is retired <em>without</em> delivery and
    /// never retried — so a build that matched nothing would drain the queue, report a clean tally, and lose all
    /// ten thousand events silently. That is the one loss the tally alone cannot see.
    /// </remarks>
    private static void AssertTheCountersAgree(ChaosRun run, IAlvoEventMeter metrics)
    {
        metrics.CountOf(DispatchedCounter).ShouldBe(
            EventCount,
            $"one increment per event whose hooks all ran and whose entry was retired; {run.Describe()}");
        metrics.CountOf(FilteredCounter).ShouldBe(
            0,
            "every seeded event matches the one declared hook, so nothing may be counted as filtered — a "
            + $"filtered event is retired without delivery and never retried. {run.Describe()}");
        metrics.CountOf(FailedCounter).ShouldBe(
            run.Refused,
            $"one increment per failed attempt, so the counter and the receiver must agree; {run.Describe()}");
        run.Accepted.ShouldBe(
            EventCount,
            "at-least-once permits more, but a second accepted delivery of one event would mean an entry was "
            + $"claimed twice while a delivery was in flight. {run.Describe()}");
    }

    /// <summary>
    /// Writes a measured number three ways, since a criterion's value is the point of these facts and not only
    /// their verdict — and, measured, only one of the three is visible on a passing run.
    /// </summary>
    /// <remarks>
    /// The same shape, and the same reasoning, as <c>PagingPerformanceTests.ReportAsync</c>:
    /// <c>TestOutputHelper</c> output is not printed on a passing test under MTP, an attachment's content lands in
    /// OS temp, and the durable copy is the line appended to <c>artifacts/criteria/events.md</c> — which
    /// <c>ci.yml</c> reads into the run summary and uploads.
    /// </remarks>
    private static async Task ReportAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.AddAttachment("events-criteria", line);
        await AppendCriterionAsync(line);
    }

    private static async Task AppendCriterionAsync(string line)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "artifacts", "criteria", "events.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(
            path,
            string.Create(CultureInfo.InvariantCulture, $"- {DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The project this criterion is measured against, and the one a subclass stands its database up from.
    /// </summary>
    /// <remarks>
    /// Composed on each read rather than cached in a static initializer, for the reason
    /// <see cref="AlvoEventCriteriaTests"/>'s own project property gives: a static field initializer runs in
    /// declaration order and would capture the members below before they were assigned.
    /// </remarks>
    protected static AlvoEventProject Project => new(Schema, Descriptor, Caller, Entity, EventMeterName);

    /// <summary>The criterion's own number.</summary>
    private const int EventCount = 10_000;

    /// <summary>
    /// One delivery in twenty is refused, so roughly five hundred events travel the release-and-retry path.
    /// </summary>
    /// <remarks>
    /// A rate rather than a fixed list of victims, so which events fail depends on the order the pump really
    /// delivered in. It is asserted as a floor rather than an exact count, because a retried delivery is itself
    /// an attempt and can be refused again.
    /// </remarks>
    private const int FailEvery = 20;

    /// <summary>
    /// How many claims are taken by a claimant that never comes back, spread over the run.
    /// </summary>
    /// <remarks>
    /// What this reproduces is the durable consequence of a dispatcher dying mid-batch — a claim nobody will ever
    /// finish, recoverable through its lease and nothing else. Killing a real process is Task 12's harness; this
    /// number must not be read as a process restart.
    /// </remarks>
    private const int AbandonedClaims = 2;

    /// <summary>The meter the event counters are published on.</summary>
    /// <remarks>
    /// Restated for the reason <see cref="AlvoEventCriteriaTests"/> restates it: the core's
    /// <c>AlvoEventMetrics</c> is <see langword="internal"/>. Drift is fail-loud rather than silent — two of the
    /// three names below are asserted with a non-zero expected count, and a listener on a meter nobody publishes
    /// answers zero.
    /// </remarks>
    private const string EventMeterName = "MMLib.Alvo.Events";

    /// <inheritdoc cref="EventMeterName"/>
    private const string DispatchedCounter = "alvo.events.dispatched";

    /// <inheritdoc cref="EventMeterName"/>
    private const string FilteredCounter = "alvo.events.filtered";

    /// <inheritdoc cref="EventMeterName"/>
    private const string FailedCounter = "alvo.events.failed";

    private const string Entity = "orders";
    private const string StatusField = "status";
    private const string ReferenceField = "reference";
    private const string QueuedStatus = "queued";
    private const string ChaosEndpoint = "chaos-sink";
    private const string ChaosEndpointUrl = "https://receiver.test/chaos";
    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

#if DEBUG
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Debug";
#else
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Release";
#endif

    /// <summary>The row image the n-th queued event carries.</summary>
    /// <remarks>
    /// Every image carries a row id of its own, so the ten thousand events are ten thousand partition keys rather
    /// than one — the arrangement a real backlog has, and the one in which nothing about the run depends on two
    /// events sharing a key.
    /// </remarks>
    private static AlvoRecord RecordFor(int index) => new(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        [AlvoManagedColumns.Id] = Guid.NewGuid(),
        [StatusField] = QueuedStatus,
        [ReferenceField] = $"CHAOS-{index:D5}",
    });

    /// <summary>The caller the seeded events are attributed to: authenticated, as the entity's rules require.</summary>
    private static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    /// <summary>
    /// The descriptor: one entity, one <c>afterCreate</c> hook every seeded event matches, and the endpoint it
    /// posts to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hook carries a real condition rather than none, so all ten thousand events are judged by the compiled
    /// CEL predicate on the way through — a hook with no condition would leave the subscription step untested at
    /// this scale. It is written so that every seeded event matches, because the criterion is about what happens
    /// to events that <em>are</em> subscribed; an event matching nothing is retired without delivery, and
    /// <see cref="AlvoEventCriteriaTests"/> owns that half.
    /// </para>
    /// <para>
    /// The action declares no <c>payload</c>, so the delivery carries the canonical envelope — which is what lets
    /// the receiver read each delivery's own event id out of the body.
    /// </para>
    /// </remarks>
    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "event-chaos-criterion",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Entity] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    [StatusField] = new() { Type = DescField.String },
                    [ReferenceField] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly,
                    Get = AuthenticatedOnly,
                    Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly,
                    Delete = AuthenticatedOnly,
                },
                Hooks = new EntityHooks
                {
                    AfterCreate =
                    [
                        new AfterHook
                        {
                            Condition = $"new.{StatusField} == '{QueuedStatus}'",
                            Action = new WebhookAction { Endpoint = ChaosEndpoint },
                        },
                    ],
                },
            },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                [ChaosEndpoint] = new() { Url = ChaosEndpointUrl, SecretRef = "chaos-sink-secret" },
            },
        },
    };

    /// <summary>
    /// The applied schema the descriptor above maps to, paired by hand for the reason
    /// <see cref="AlvoEventCriteriaTests"/> gives: the core's mapper is <see langword="internal"/> and
    /// unreachable from a test project's own suite.
    /// </summary>
    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Entity,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = StatusField, Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = ReferenceField, Type = SchemaField.String, Nullable = true },
            ],
        },
    ]);
}
