using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Tests.Data;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The SQLite write gate: this process's writers to one database queue in one line instead of meeting the engine's busy
/// lock and <c>Microsoft.Data.Sqlite</c>'s sleeping retry.
/// </summary>
public sealed class SqliteWriteGateTests : IDisposable
{
    private const string Subscriber = "send-confirmation";
    private const int Claimants = 64;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-write-gate-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public void One_database_under_two_spellings_shares_one_gate_and_another_database_does_not()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _databasePath);

        var first = SqliteWriteGates.For($"Data Source={relative}");
        var second = SqliteWriteGates.For($"Data Source={_databasePath};Pooling=False;Default Timeout=5");
        var other = SqliteWriteGates.For($"Data Source={_databasePath}.other");

        second.ShouldBeSameAs(first);
        other.ShouldNotBeSameAs(first);
    }

    [Fact]
    public void Use_sqlite_hands_the_stores_the_gate_of_the_database_it_names()
    {
        var connectionString = $"Data Source={_databasePath}";
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite(connectionString));
        using var services = collection.BuildServiceProvider();

        services.GetRequiredService<RelationalConnectionFactory>().WriteGate.ShouldBeSameAs(SqliteWriteGates.For(connectionString));
    }

    [Fact]
    public async Task The_gate_admits_one_writer_at_a_time()
    {
        var gate = new SerialRelationalWriteGate();
        var inside = 0;
        var most = 0;

        await Task.WhenAll(Enumerable.Range(0, Claimants).Select(_ => Task.Run(async () =>
        {
            using (await gate.EnterAsync(Ct))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                await Task.Delay(1, Ct);
                Interlocked.Decrement(ref inside);
            }
        }, Ct)));

        most.ShouldBe(1);
    }

    /// <summary>
    /// A writer cancelled while it waits takes nothing with it, and a turn disposed twice is released once: after both, the
    /// gate still admits exactly one writer.
    /// </summary>
    [Fact]
    public async Task A_writer_cancelled_at_the_gate_leaks_no_turn_and_a_turn_is_released_once()
    {
        var gate = new SerialRelationalWriteGate();
        var holder = await gate.EnterAsync(Ct);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancel.Token).AsTask();

        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(waiting);
        holder.Dispose();
        holder.Dispose();

        using var next = await gate.EnterAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var after = gate.EnterAsync(Ct).AsTask();
        after.IsCompleted.ShouldBeFalse("a second release would have let a second writer in beside the first");
        next.Dispose();
        (await after.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
    }

    /// <summary>
    /// Sixty-four claimants on real threads, each claiming and then answering in its own write transaction, over a
    /// connection whose busy retry gives up after one second: queued at the gate, none meets the busy database, and every
    /// delivery is taken once.
    /// </summary>
    /// <remarks>
    /// Without the gate this fails: the claimants reach SQLite together, and <c>Microsoft.Data.Sqlite</c>'s sleeping retry
    /// outlasts a one-second <c>Default Timeout</c> for some of them, which then throw <c>database is locked</c>.
    /// </remarks>
    [Fact]
    public async Task Sixty_four_claimants_on_threads_queue_at_the_gate_and_none_meets_a_busy_database()
    {
        var connectionString = $"Data Source={_databasePath};Pooling=False;Default Timeout=1";
        await using var world = await DeliveryStoreWorld.StartAsync(() => new SqliteConnection(connectionString), SqliteWriteGates.For(connectionString));
        await FanOutLanesAsync(world);

        var claimed = await Task.WhenAll(Enumerable.Range(0, Claimants).Select(index => Task.Run(() => ClaimAndAnswerAsync(world.Store, $"worker-{index}"), Ct)));

        var ids = claimed.SelectMany(batch => batch).ToList();
        ids.ShouldBeUnique();
        ids.Count.ShouldBe(Claimants);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task FanOutLanesAsync(DeliveryStoreWorld world)
    {
        foreach (var key in Enumerable.Range(0, Claimants).Select(index => $"tickets:{index}"))
        {
            var request = new DeliveryRequest { EventId = await world.AppendEventAsync(key), Subscriber = Subscriber, PartitionKey = key, Kind = "handler" };
            await world.Store.FanOutAsync([request], Ct);
        }
    }

    private static async Task<List<Guid>> ClaimAndAnswerAsync(IDeliveryStore store, string claimant)
    {
        var taken = new List<Guid>();
        foreach (var delivery in await store.ClaimAsync(ClaimFor(claimant), Ct))
        {
            taken.Add(delivery.Id);
            (await store.CompleteAsync(AttemptOf(delivery, claimant), Ct)).ShouldBeTrue();
        }

        return taken;
    }

    private static DeliveryClaim ClaimFor(string claimant) => new()
    {
        Claimant = claimant,
        Ordered = [Subscriber],
        BatchSize = 1,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
        OutboxClaimLease = TimeSpan.FromMinutes(5),
    };

    private static DeliveryAttempt AttemptOf(ClaimedDelivery delivery, string claimant) =>
        new() { Delivery = delivery, Claimant = claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(1) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
