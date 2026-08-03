using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Events;

using System.Diagnostics;
using System.Globalization;

using Xunit;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — both driver
// test projects are granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// One started backend the 10 000-event chaos criterion runs over: the production writer filling the real
/// <c>alvo_outbox</c>, the real dispatcher draining it, a receiver that refuses one delivery in n, and a
/// claimant that takes a batch and never comes back.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both driver test projects rather than copied, for the reason <see cref="AlvoEventCriteriaWorld"/>
/// is: "no event is lost" is an engine-agnostic guarantee, and two per-engine copies of the seed, the drain and
/// the chaos are two chances for the engines to stop being asked the same question. Everything engine-specific
/// arrives as the started database.
/// </para>
/// <para>
/// <b>The seed goes through <see cref="OutboxTable.InsertAsync"/> — the production writer — in transactions of
/// <see cref="SeedTransactionSize"/>, and the envelope is built by the production
/// <see cref="OutboxEventFactory"/>.</b> Ten thousand writes through <see cref="IAlvoData"/> would measure the
/// write path, which has its own suites, and would put the criterion's cost in the wrong place; but a hand-built
/// row is the classic way a seeded criterion comes to measure a queue nothing can read, so nothing here is
/// hand-built except the row images. <see cref="ClaimOneAndReleaseAsync"/> is the guard that earns the
/// shortcut: one seeded entry is claimed and deserialized before the run starts.
/// </para>
/// <para>
/// <b>Time moves by hand.</b> A claim's lease is the only thing that recovers an entry whose claimant never came
/// back, and the shipped default is five minutes — so <see cref="AbandonAClaimAsync"/> advances a fake clock past
/// it instead of the run waiting. What that reproduces is the durable <em>consequence</em> of a dispatcher dying
/// mid-batch, not the death itself; killing a process is Task 12's harness, and this world must not be read as
/// doing it.
/// </para>
/// </remarks>
internal sealed class OutboxChaosWorld : IAsyncDisposable
{
    /// <summary>
    /// Stands the world up: builds the receiver, the clock and the meter listener, hands their registrations to
    /// <paramref name="startDatabase"/>, and resolves the dispatcher out of the container it built.
    /// </summary>
    /// <param name="project">The entity, schema, descriptor, caller and meter the criterion is measured on.</param>
    /// <param name="failEvery">Every n-th delivery attempt the receiver refuses.</param>
    /// <param name="startDatabase">
    /// Starts one engine's database, applying the registrations it is handed before the provider is built. The
    /// per-engine fixture is the only thing that differs between the two legs.
    /// </param>
    internal static async Task<OutboxChaosWorld> StartAsync(
        AlvoEventProject project,
        int failEvery,
        Func<Action<IServiceCollection>, Task<IServiceProvider>> startDatabase)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(startDatabase);

        var receiver = new ChaosWebhookReceiver(failEvery);
        var clock = new AdvanceableClock(_seedInstant);
        var meter = new RecordingMeterListener(project.MeterName);
        var services = await startDatabase(container => Install(container, receiver, clock));
        var world = new OutboxChaosWorld(project, services, receiver, clock, meter);
        await world.EnsureQueueAsync();

        return world;
    }

    /// <summary>The event counters this run incremented.</summary>
    internal IAlvoEventMeter Metrics => _meter;

    /// <summary>How many outbox entries are still undelivered, and how many have been retired.</summary>
    internal Task<AlvoOutboxTally> TallyAsync() => _tally.TallyAsync();

    /// <summary>
    /// Appends <paramref name="count"/> events to the outbox through the production writer and answers their
    /// ids, ascending — the order a claim takes them in.
    /// </summary>
    /// <param name="count">How many events to queue.</param>
    /// <param name="recordFor">The row image the n-th event carries; the suite owns the shape.</param>
    /// <remarks>
    /// The rows are <b>appended in reverse id order</b>, which is the arrangement
    /// <see cref="IOutboxStoreWorld.SeedAsync"/> already uses and for the same measured reason: appended
    /// ascending, an engine's physical row order equals the queue order, so a claim comes back sorted whether or
    /// not anything sorted it.
    /// </remarks>
    internal async Task<ChaosSeed> SeedAsync(int count, Func<int, AlvoRecord> recordFor)
    {
        ArgumentNullException.ThrowIfNull(recordFor);

        var stopwatch = Stopwatch.StartNew();
        var events = Minted(count, recordFor);
        foreach (var batch in events.Reverse().Chunk(SeedTransactionSize))
        {
            await AppendAsync(batch);
        }

        return new ChaosSeed([.. events.Select(@event => @event.Id)], stopwatch.Elapsed);
    }

    /// <summary>
    /// Claims one entry, reads its stored envelope, and hands it straight back.
    /// </summary>
    /// <returns>The claimed entry's envelope, or <see langword="null"/> when nothing was claimable at all.</returns>
    /// <remarks>
    /// The guard that earns the direct seed, and the twin of <c>PagingPerformanceTests</c>' read-back through the
    /// API: a seed that stored an envelope the dispatch path cannot read would otherwise leave every delivery
    /// below missing and the failure would name the wrong cause. The release is what keeps the entry in the run:
    /// its attempt count is deliberately not rolled back, exactly as a real failed delivery's is not.
    /// </remarks>
    internal async Task<AlvoEvent?> ClaimOneAndReleaseAsync()
    {
        var claimed = await _store.ClaimAsync(SetupProbe, 1, _options.MaxAttempts, _options.ClaimLease, Ct);
        if (claimed.Count == 0)
        {
            return null;
        }

        var entry = claimed[0];
        var @event = AlvoEventJson.Read(entry.Payload);
        await _store.ReleaseAsync(entry.Id, Ct);

        return @event;
    }

    /// <summary>
    /// Drains the queue with the dispatcher's own pump, abandoning a claim <paramref name="abandonedClaims"/>
    /// times on the way.
    /// </summary>
    /// <param name="abandonedClaims">How many claims are taken by a claimant that never comes back.</param>
    /// <param name="queued">How many entries the seed queued, which is what spaces the abandonments out.</param>
    /// <remarks>
    /// <para>
    /// <b>The pump is the dispatcher's own <c>PumpOneBatchAsync</c>, and the loop is bounded.</b> Sleeping past a
    /// poll interval would make every count after it an under-count; an unbounded loop would turn a queue whose
    /// entries keep being released into a hung job rather than a named failure. The bound being reached is
    /// reported as <see cref="ChaosRun.HitClaimCap"/> and asserted, so it cannot pass as a drain.
    /// </para>
    /// <para>
    /// <b>An empty claim ends the run and does not end the fact.</b> Whether the queue was really drained is the
    /// tally's answer, taken after the loop and asserted by the criterion — so a pump that gave up while entries
    /// were still pending fails on the number of entries left rather than on the loop's own opinion.
    /// </para>
    /// </remarks>
    internal async Task<ChaosRun> RunWithChaosAsync(int abandonedClaims, int queued)
    {
        var stopwatch = Stopwatch.StartNew();
        var abandonBefore = AbandonPoints(abandonedClaims, queued);
        var claims = 0;

        while (claims < MaxClaims)
        {
            if (abandonBefore.Contains(claims))
            {
                await AbandonAClaimAsync();
            }

            claims++;
            if (await _dispatcher.PumpOneBatchAsync(Ct) == 0)
            {
                break;
            }
        }

        return await FinishAsync(stopwatch.Elapsed, claims);
    }

    /// <summary>
    /// Disposes the meter listener, and nothing else: the container and the database belong to the fixture that
    /// started them.
    /// </summary>
    /// <remarks>
    /// The event counters are process-wide statics, so a listener that outlived its world would keep summing the
    /// next one's measurements into this one's totals.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        _meter.Dispose();

        return ValueTask.CompletedTask;
    }

    private OutboxChaosWorld(
        AlvoEventProject project,
        IServiceProvider services,
        ChaosWebhookReceiver receiver,
        AdvanceableClock clock,
        RecordingMeterListener meter)
    {
        _entity = project.Schema.Entities.Single(
            entity => string.Equals(entity.Name, project.Entity, StringComparison.Ordinal));
        _caller = project.Caller;
        _receiver = receiver;
        _clock = clock;
        _meter = meter;
        _store = services.GetRequiredService<IOutboxStore>();
        _connections = services.GetRequiredService<RelationalConnectionFactory>();
        _options = services.GetRequiredService<IOptions<AlvoEventOptions>>().Value;
        _tally = new OutboxTallyProbe(services);
        _dispatcher = services.GetServices<IHostedService>().OfType<OutboxDispatcher>().Single();
    }

    /// <summary>
    /// The two registrations a chaos run needs: the socket under the named client, and the clock the claim
    /// lease is measured on.
    /// </summary>
    /// <remarks>
    /// Applied after <c>AddAlvo</c>, so the clock replaces the driver's <c>TryAddSingleton(TimeProvider.System)</c>
    /// by being the last registration for the service type — the same seam a host owns. Nothing else is
    /// substituted: the queue, the claim, the subscription decision and the action are all the shipped ones.
    /// </remarks>
    private static void Install(IServiceCollection services, ChaosWebhookReceiver receiver, TimeProvider clock)
    {
        services.AddSingleton(clock);
        services.AddHttpClient(WebhookDelivery.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => receiver);
    }

    /// <summary>
    /// Brings the queue's storage up, through the port's own <see cref="IOutboxStore.EnsureAsync"/>.
    /// </summary>
    /// <remarks>
    /// This is the one line of the dispatcher's <c>PumpUntilStoppedAsync</c> that driving
    /// <c>PumpOneBatchAsync</c> directly skips, so the world performs it rather than the seed depending on some
    /// other component having happened to create the table first. It is the shipped call, not a hand-written
    /// <c>CREATE TABLE</c>: a seed against a table this suite created itself would be a seed against a shape the
    /// driver does not use.
    /// </remarks>
    private Task EnsureQueueAsync() => _store.EnsureAsync(Ct);

    private IReadOnlyList<AlvoEvent> Minted(int count, Func<int, AlvoRecord> recordFor) =>
        [.. Enumerable.Range(0, count).Select(index => EventFor(recordFor(index)))];

    /// <summary>
    /// One event exactly as a create through the data path would emit it, built by the production factory so the
    /// stored envelope is the shipped shape rather than this suite's idea of it.
    /// </summary>
    private AlvoEvent EventFor(AlvoRecord record) => OutboxEventFactory.For(
        _entity, OutboxOperation.Created, _caller, _clock.GetUtcNow(), record, preImage: null);

    /// <summary>Appends one transaction's worth of events, on one connection of its own.</summary>
    private async Task AppendAsync(IReadOnlyList<AlvoEvent> batch)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var @event in batch)
                {
                    await OutboxTable.InsertAsync(connection, transaction, _tableName, @event, Ct);
                }

                await transaction.CommitAsync(Ct);
            }
        }
    }

    /// <summary>
    /// Which claims are preceded by an abandoned one, spread over the run rather than bunched at its start.
    /// </summary>
    /// <remarks>
    /// A restart before the first claim would leave the whole run to recover from it, and two at the very end
    /// would leave nothing after them — either way the pump's own recovery would not be measured mid-flight.
    /// </remarks>
    private HashSet<int> AbandonPoints(int abandonedClaims, int queued)
    {
        var expected = Math.Max(1, queued / _options.BatchSize);

        return [.. Enumerable.Range(1, abandonedClaims).Select(nth => expected * nth / (abandonedClaims + 1))];
    }

    /// <summary>
    /// Takes a claim as a claimant that never comes back — no delivery, no retire, no release — and moves time
    /// past the lease so the entries are recoverable at all.
    /// </summary>
    /// <remarks>
    /// This is the shape a dispatcher that died mid-batch leaves behind, and the lease is the only mechanism that
    /// recovers it: nothing marks the entries delivered and nothing hands them back. Advancing the clock is what
    /// makes the recovery observable inside a test's runtime rather than five minutes later.
    /// </remarks>
    private async Task AbandonAClaimAsync()
    {
        var abandoned = await _store.ClaimAsync(
            DeadClaimant, AbandonedClaimSize, _options.MaxAttempts, _options.ClaimLease, Ct);

        _abandonedClaims++;
        _abandonedIds.AddRange(abandoned.Select(entry => entry.Id));
        _clock.Advance(_options.ClaimLease + _leaseOvershoot);
    }

    private async Task<ChaosRun> FinishAsync(TimeSpan elapsed, int claims) => new(
        Attempts: _receiver.Attempts,
        Accepted: _receiver.Accepted,
        AcceptedIds: _receiver.AcceptedIds,
        Refused: _receiver.Refused,
        RefusedIds: _receiver.RefusedIds,
        AbandonedClaims: _abandonedClaims,
        AbandonedIds: [.. _abandonedIds],
        Claims: claims,
        HitClaimCap: claims >= MaxClaims,
        Tally: await TallyAsync(),
        Elapsed: elapsed);

    private const string SchemaPrefix = "alvo";

    /// <summary>Who the setup probe claims as, so its one claim is attributable in a stored row.</summary>
    private const string SetupProbe = "setup-probe";

    /// <summary>Who the abandoned claims are taken by — a name a reader recognises in <c>claimed_by</c>.</summary>
    private const string DeadClaimant = "dead-replica";

    /// <summary>How many entries one abandoned claim takes with it.</summary>
    private const int AbandonedClaimSize = 10;

    /// <summary>
    /// How many events one seeding transaction carries. Big enough that ten thousand rows are ten commits rather
    /// than ten thousand, small enough that no engine is asked to hold the whole seed in one transaction.
    /// </summary>
    private const int SeedTransactionSize = 1_000;

    /// <summary>
    /// How many claims a run may take before it gives up. Generous against ten thousand events over a batch size
    /// of 100 with a few hundred redeliveries, and still bounded — a queue whose entries keep being released
    /// fails by name instead of looping forever.
    /// </summary>
    private const int MaxClaims = 400;

    /// <summary>How far past a lease the clock moves, so an abandoned claim is unambiguously stale.</summary>
    private static readonly TimeSpan _leaseOvershoot = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The instant the seed is stamped with. Fixed, so a run's stored rows read the same on every engine; the
    /// ids stay strictly ascending regardless, because <see cref="AlvoEventId"/> is monotonic per process.
    /// </summary>
    private static readonly DateTimeOffset _seedInstant = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

    private readonly EntitySchema _entity;
    private readonly AlvoContext _caller;
    private readonly ChaosWebhookReceiver _receiver;
    private readonly AdvanceableClock _clock;
    private readonly RecordingMeterListener _meter;
    private readonly IOutboxStore _store;
    private readonly RelationalConnectionFactory _connections;
    private readonly AlvoEventOptions _options;
    private readonly OutboxTallyProbe _tally;
    private readonly OutboxDispatcher _dispatcher;
    private readonly List<Guid> _abandonedIds = [];
    private readonly string _tableName = OutboxTable.NameFor(SchemaPrefix);

    private int _abandonedClaims;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clock the world moves by hand, so a claim lease expires without anyone waiting for one.</summary>
    private sealed class AdvanceableClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;

        private DateTimeOffset _now = start;
    }
}

/// <summary>What one seed produced.</summary>
/// <param name="Ids">Every queued event's id, ascending — the order a claim takes them in.</param>
/// <param name="Elapsed">
/// How long the seed itself took, reported apart from the run because it is setup cost and folding it in would
/// make the number mean something other than what the criterion says.
/// </param>
internal sealed record ChaosSeed(IReadOnlyList<Guid> Ids, TimeSpan Elapsed);

/// <summary>What one chaos run did, in the numbers a reader has to act on.</summary>
/// <param name="Attempts">Every delivery attempt the receiver saw.</param>
/// <param name="Accepted">How many of them it accepted.</param>
/// <param name="AcceptedIds">The distinct events at least one accepted delivery carried.</param>
/// <param name="Refused">How many attempts it refused.</param>
/// <param name="RefusedIds">The distinct events at least one refused attempt carried.</param>
/// <param name="AbandonedClaims">How many claims were taken by a claimant that never came back.</param>
/// <param name="AbandonedIds">The entries those claims took with them.</param>
/// <param name="Claims">How many batches the pump claimed.</param>
/// <param name="HitClaimCap">Whether the run stopped on its own bound instead of on an empty claim.</param>
/// <param name="Tally">The outbox's state when the run ended.</param>
/// <param name="Elapsed">How long the run took, seeding excluded.</param>
internal sealed record ChaosRun(
    int Attempts,
    int Accepted,
    IReadOnlySet<Guid> AcceptedIds,
    int Refused,
    IReadOnlySet<Guid> RefusedIds,
    int AbandonedClaims,
    IReadOnlyList<Guid> AbandonedIds,
    int Claims,
    bool HitClaimCap,
    AlvoOutboxTally Tally,
    TimeSpan Elapsed)
{
    /// <summary>Every number the run produced, on one line, so a failure reads as a measurement.</summary>
    internal string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"accepted={Accepted} distinct={AcceptedIds.Count} attempts={Attempts} refused={Refused} "
        + $"(distinct {RefusedIds.Count}) abandoned={AbandonedIds.Count} entries over {AbandonedClaims} claims "
        + $"claims={Claims} cap-hit={HitClaimCap} pending={Tally.Pending} retired={Tally.Retired} "
        + $"in {Elapsed.TotalSeconds:F1}s");
}
