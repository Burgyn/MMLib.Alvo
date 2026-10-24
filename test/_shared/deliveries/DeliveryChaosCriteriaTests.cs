using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.TestHost;
using MMLib.Alvo.TestHost.Tests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Xunit;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// <c>baas-analyza.md:676</c> for event handlers: a <b>10 000-event chaos run loses nothing and keeps every record's
/// order</b> — 2 500 records with four events each reach their handler, through refused attempts and abandoned
/// claims, over the real outbox, fan-out and delivery store.
/// </summary>
public abstract class DeliveryChaosCriteriaTests
{
    private const int EventCount = 10_000;
    private const int EventsPerRecord = 4;
    private const int RecordCount = EventCount / EventsPerRecord;
    private const int FailEvery = 20;
    private const int AbandonedClaims = 2;
    private const int AbandonEveryCalls = 2_500;
    private const int SeedBatch = 100;
    private const int MaxRounds = 5_000;
    private const string Sink = "chaos-sink";
    private const string Orders = "orders";

    private static readonly AlvoContext _agent = AlvoTestHost.Caller("agent");

    /// <summary>The engine and build configuration a reported number was measured under.</summary>
    protected abstract string EngineDescription { get; }

    /// <summary>Registers this leg's database on the builder, and a raw connection to read the delivery table.</summary>
    protected abstract Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync();

    [Fact]
    public async Task Ten_thousand_events_reach_their_handler_in_order_and_none_is_lost()
    {
        EventCount.ShouldBe(10_000, "the criterion is ten thousand events; a smaller number is a decision made here");
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var sink = new ChaosSink();
        await using var alvo = await StartAsync(database.Use, clock, sink);

        var seeded = await SeedAsync(alvo);
        var stopwatch = Stopwatch.StartNew();
        var run = await RunWithChaosAsync(alvo, clock, sink);
        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);

        await ReportAsync($"§3 delivery chaos ({EventCount} events on {RecordCount} records, fail 1-in-{FailEvery}, "
            + $"{AbandonedClaims} abandoned claims, {EngineDescription}): {run.Rounds} rounds, {sink.AcceptedEvents.Count} accepted, "
            + $"{sink.Refused.Count} refused, {stopwatch.Elapsed.TotalSeconds:F1}s");

        AssertNothingIsLost(seeded, sink, rows, run);
        AssertEveryRecordKeptItsOrder(sink);
        AssertTheChaosHappened(sink, run);
    }

    private static async Task<AlvoTestHost> StartAsync(Action<IAlvoBuilder> use, FakeTimeProvider clock, ChaosSink sink) =>
        await AlvoTestHost.StartAsync(
            builder =>
            {
                use(builder);
                builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "chaos-desk.alvo.json"));
                builder.Entity(Orders, orders => orders.OnSaved(
                    Sink, sink.HandleAsync, configure: options => options.Retry = AlvoRetryPolicy.From([.. Enumerable.Repeat(TimeSpan.FromSeconds(1), 12)])));
                builder.Services.Configure<AlvoEventOptions>(options => options.BatchSize = 500);
                builder.Services.Configure<AlvoDeliveryOptions>(options => options.MaxConcurrency = 64);
            },
            clock,
            cancellationToken: Ct);

    /// <summary>Creates every record (step 0), then writes steps 1 to 3 to each — four events per record, ten thousand in all.</summary>
    private static async Task<HashSet<string>> SeedAsync(AlvoTestHost alvo)
    {
        var ids = await CreateRecordsAsync(alvo);
        for (var step = 1; step < EventsPerRecord; step++)
        {
            foreach (var batch in ids.Chunk(SeedBatch))
            {
                IReadOnlyList<AlvoRowPatch> patches = [.. batch.Select(id => new AlvoRowPatch(id, Step(step)))];
                await alvo.Data.UpdateManyAsync(Orders, patches, _agent, cancellationToken: Ct);
            }
        }

        var seeded = ids.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        seeded.Count.ShouldBe(RecordCount, "'nothing was lost' is trivially true of an empty queue");

        return seeded;
    }

    private static async Task<List<Guid>> CreateRecordsAsync(AlvoTestHost alvo)
    {
        var ids = new List<Guid>();
        for (var batch = 0; batch < RecordCount / SeedBatch; batch++)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                [.. Enumerable.Range(0, SeedBatch).Select(_ => new Dictionary<string, object?> { ["step"] = 0L })];
            var result = await alvo.Data.CreateManyAsync(Orders, rows, _agent, cancellationToken: Ct);
            ids.AddRange(result.Rows.Select(row => (Guid)row[AlvoManagedColumns.Id]!));
        }

        return ids;
    }

    private static Dictionary<string, object?> Step(int step) => new() { ["step"] = (long)step };

    private static async Task<ChaosRun> RunWithChaosAsync(AlvoTestHost alvo, FakeTimeProvider clock, ChaosSink sink)
    {
        var pump = alvo.Services.GetRequiredService<IAlvoEventPump>();
        sink.Arm(alvo.Services.GetRequiredService<IDeliveryStore>(), Ct);
        var rounds = 0;
        while (sink.AcceptedEvents.Count < EventCount && rounds < MaxRounds)
        {
            var result = await pump.DrainAsync(Ct);
            rounds++;
            if (result.IsQuiet)
            {
                clock.Advance(TimeSpan.FromMinutes(2));
            }
        }

        return new ChaosRun(rounds, [.. sink.Abandoned]);
    }

    private static DeliveryClaim DeadWorkerClaim() => new()
    {
        Claimant = "dead-worker",
        Ordered = [Sink],
        BatchSize = 1,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
        OutboxClaimLease = TimeSpan.FromMinutes(5),
    };

    private static void AssertNothingIsLost(HashSet<string> seeded, ChaosSink sink, IReadOnlyList<DeliveryRow> rows, ChaosRun run)
    {
        run.Rounds.ShouldBeLessThan(MaxRounds, "the pump ran out of rounds instead of draining the queue");
        sink.AcceptedEvents.Count.ShouldBe(EventCount, "every event reaches the handler at least once");
        sink.Steps.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(seeded).ShouldBeTrue("every seeded record's events reached the handler");
        rows.Count.ShouldBe(EventCount, "one delivery per event, never two: the fan-out is unique on (event, subscriber)");
        rows.ShouldAllBe(row => row.Status == "succeeded", "nothing is left pending, retrying or dead");
    }

    /// <summary>Q7: one record's events reach an ordered handler in order, whatever failed or was abandoned between them (M7).</summary>
    private static void AssertEveryRecordKeptItsOrder(ChaosSink sink)
    {
        var outOfOrder = sink.Steps.Where(record => !record.Value.SequenceEqual(Enumerable.Range(0, EventsPerRecord))).Select(record => record.Key).ToList();
        outOfOrder.ShouldBeEmpty($"{outOfOrder.Count} records saw their events out of order");
    }

    private static void AssertTheChaosHappened(ChaosSink sink, ChaosRun run)
    {
        sink.Refused.Count.ShouldBeGreaterThan(EventCount / (FailEvery * 2), "the chaos must have happened, not only been configured");
        sink.Refused.Keys.ShouldAllBe(id => sink.AcceptedEvents.ContainsKey(id), "a refused delivery is retried until it lands");
        run.Abandoned.Count.ShouldBe(AbandonedClaims, "a claim must have been abandoned mid-run");
        run.Abandoned.ShouldAllBe(id => sink.AcceptedEvents.ContainsKey(id), "an abandoned claim is recovered by its lease and nothing else");
    }

    private static async Task ReportAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.AddAttachment("events-criteria", line);
        var path = Path.Combine(RepositoryRoot.Find(), "artifacts", "criteria", "events.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(
            path, string.Create(CultureInfo.InvariantCulture, $"- {DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}"), Ct);
    }

#if DEBUG
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Debug";
#else
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Release";
#endif

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ChaosRun(int Rounds, IReadOnlyList<Guid> Abandoned);

    /// <summary>
    /// The handler: refuses one call in <see cref="FailEvery"/>; remembers every event it accepted and, per record, the
    /// order in which that record's steps arrived; and every <see cref="AbandonEveryCalls"/> calls has a second claimant
    /// take one lane head and never answer for it.
    /// </summary>
    /// <remarks>
    /// The abandoned claim is taken <b>while a round is running</b>, from inside the handler, because a manual round
    /// claims every lane head the outbox has fanned out so far: between two rounds there is nothing left to abandon, and a
    /// claim taken there comes back empty. A claim that still comes back empty is not counted, and is tried again on the
    /// next call.
    /// </remarks>
    private sealed class ChaosSink
    {
        private int _abandoning;
        private int _calls;
        private IDeliveryStore? _store;
        private CancellationToken _ct;

        internal ConcurrentQueue<Guid> Abandoned { get; } = new();

        internal ConcurrentDictionary<Guid, byte> AcceptedEvents { get; } = new();

        internal ConcurrentDictionary<string, ConcurrentQueue<int>> Steps { get; } = new(StringComparer.Ordinal);

        internal ConcurrentDictionary<Guid, byte> Refused { get; } = new();

        internal void Arm(IDeliveryStore store, CancellationToken ct)
        {
            _store = store;
            _ct = ct;
        }

        internal async Task HandleAsync(AlvoSaved<AlvoRecord> saved, AlvoEventContext context)
        {
            var call = Interlocked.Increment(ref _calls);
            await AbandonAClaimIfDueAsync(call);
            if (call % FailEvery == 0)
            {
                Refused.TryAdd(context.EventId, 0);
                throw new InvalidOperationException("chaos");
            }

            if (AcceptedEvents.TryAdd(context.EventId, 0))
            {
                Steps.GetOrAdd(saved.New[AlvoManagedColumns.Id]!.ToString()!, _ => new()).Enqueue(Convert.ToInt32(saved.New["step"], CultureInfo.InvariantCulture));
            }
        }

        private bool IsAbandonDue(int call) =>
            Abandoned.Count < AbandonedClaims && call >= (Abandoned.Count + 1) * AbandonEveryCalls;

        private async Task AbandonAClaimIfDueAsync(int call)
        {
            if (_store is null || !IsAbandonDue(call) || Interlocked.Exchange(ref _abandoning, 1) == 1)
            {
                return;
            }

            try
            {
                if (!IsAbandonDue(call))
                {
                    return;
                }

                foreach (var delivery in await _store.ClaimAsync(DeadWorkerClaim(), _ct))
                {
                    Abandoned.Enqueue(delivery.EventId);
                }
            }
            finally
            {
                Volatile.Write(ref _abandoning, 0);
            }
        }
    }
}
