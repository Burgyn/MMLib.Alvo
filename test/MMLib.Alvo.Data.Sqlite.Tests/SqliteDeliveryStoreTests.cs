using Microsoft.Data.Sqlite;

using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The whole <see cref="DeliveryStoreContractTests"/> suite against a real SQLite database file, on the shipped connection
/// string, and the one fact the suite cannot make overlap on this engine: claimants on several threads at once.
/// </summary>
public sealed class SqliteDeliveryStoreTests : DeliveryStoreContractTests, IDisposable
{
    private const string Subscriber = "send-confirmation";
    private const int Lanes = 60;
    private const int Claimants = 8;
    private const int Rounds = 5;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-delivery-store-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Claimants on separate threads, each claiming and then answering in its own write transaction, wait for SQLite's
    /// write lock instead of failing with <c>SQLITE_BUSY</c>, and take every delivery exactly once.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.Sqlite</c>'s async calls run synchronously, so the contract's two claimants under
    /// <c>Task.WhenAll</c> never overlap on this engine; only real threads put two writers on the file at once. The
    /// connection string is the shipped one: the wait is the provider's own <c>DefaultTimeout</c> retry, as the
    /// outbox's spike Q5 measured.
    /// </remarks>
    [Fact]
    public async Task Claimants_on_several_threads_wait_for_the_write_lock_and_take_every_delivery_once()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "A Windows runner's per-write cost (~1.3 s a write on CI) outlasts the busy timeout under this load; tracked by #410, held on Linux and macOS.");

        await using var world = await DeliveryStoreWorld.StartAsync(Connect);
        await FanOutLanesAsync(world);

        var claimed = await Task.WhenAll(Enumerable.Range(0, Claimants).Select(index => Task.Run(() => ClaimAndAnswerAsync(world.Store, $"worker-{index}"), Ct)));

        var ids = claimed.SelectMany(batch => batch).ToList();
        ids.ShouldBeUnique();
        ids.Count.ShouldBe(Lanes);
    }

    /// <summary>
    /// The claim's look at a lane head's earlier deliveries seeks the partial index over the unfinished and dead rows, so a
    /// lane's succeeded history — never purged — is not walked on every claim of its head.
    /// </summary>
    [Fact]
    public async Task The_lane_check_seeks_the_partial_index_over_unfinished_deliveries()
    {
        await using var world = await DeliveryStoreWorld.StartAsync(Connect);

        var plan = await world.ExplainClaimAsync("EXPLAIN QUERY PLAN");

        plan.ShouldContain("SEARCH earlier USING INDEX ix_alvo_deliveries_lane_open (subscriber=? AND partition_key=? AND event_id<?)");
    }

    protected override async Task<IDeliveryStoreWorld> WorldAsync() => await DeliveryStoreWorld.StartAsync(Connect);

    public void Dispose()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private SqliteConnection Connect() => new($"Data Source={_databasePath};Pooling=False");

    private static async Task FanOutLanesAsync(DeliveryStoreWorld world)
    {
        foreach (var key in Enumerable.Range(0, Lanes).Select(index => $"tickets:{index}"))
        {
            var request = new DeliveryRequest { EventId = await world.AppendEventAsync(key), Subscriber = Subscriber, PartitionKey = key, Kind = "handler" };
            await world.Store.FanOutAsync([request], Ct);
        }
    }

    private static async Task<List<Guid>> ClaimAndAnswerAsync(IDeliveryStore store, string claimant)
    {
        var taken = new List<Guid>();
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var delivery in await store.ClaimAsync(ClaimFor(claimant), Ct))
            {
                taken.Add(delivery.Id);
                (await store.CompleteAsync(AttemptOf(delivery, claimant), Ct)).ShouldBeTrue();
            }
        }

        return taken;
    }

    private static DeliveryClaim ClaimFor(string claimant) => new()
    {
        Claimant = claimant,
        Ordered = [Subscriber],
        BatchSize = 3,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
        OutboxClaimLease = TimeSpan.FromMinutes(5),
    };

    private static DeliveryAttempt AttemptOf(ClaimedDelivery delivery, string claimant) =>
        new() { Delivery = delivery, Claimant = claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(1) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
