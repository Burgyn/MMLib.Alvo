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

    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(10);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-write-gate-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public void One_database_under_two_spellings_shares_one_gate_and_another_database_does_not()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _databasePath);

        var first = SqliteWriteGates.For($"Data Source={relative}");
        var second = SqliteWriteGates.For($"Data Source={_databasePath};Pooling=False;Default Timeout=5");
        var other = SqliteWriteGates.For($"Data Source={_databasePath}.other");

        second.Line.ShouldBeSameAs(first.Line);
        other.Line.ShouldNotBeSameAs(first.Line);
    }

    /// <summary>
    /// A named shared-cache in-memory database is one database to every connection that names it, so its spellings share
    /// a gate and another name gets its own. A private <c>:memory:</c> database is a new database per connection, which
    /// no gate can tell apart: every one of them shares the one <c>:memory:</c> key — extra queueing, never a missed one.
    /// </summary>
    [Fact]
    public void A_shared_in_memory_database_is_keyed_by_its_name_and_private_ones_share_one_key()
    {
        var name = $"alvo-gate-{Guid.NewGuid():N}";

        var first = SqliteWriteGates.For($"Data Source={name};Mode=Memory;Cache=Shared");
        var second = SqliteWriteGates.For($"Mode=Memory;Cache=Shared;Data Source={name};Pooling=False");
        var other = SqliteWriteGates.For($"Data Source={name}-other;Mode=Memory;Cache=Shared");

        second.Line.ShouldBeSameAs(first.Line);
        other.Line.ShouldNotBeSameAs(first.Line);
        SqliteWriteGates.For("Data Source=:memory:").Line.ShouldBeSameAs(SqliteWriteGates.For("Data Source=:memory:;Pooling=False").Line);
        SqliteWriteGates.DatabaseKey("Data Source=:memory:").ShouldBe("memory::memory:");
    }

    [Fact]
    public void Use_sqlite_hands_the_stores_the_gate_of_the_database_it_names()
    {
        var connectionString = $"Data Source={_databasePath}";
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite(connectionString));
        using var services = collection.BuildServiceProvider();

        var gate = services.GetRequiredService<RelationalConnectionFactory>().WriteGate.ShouldBeOfType<SqliteWriteGate>();
        gate.Line.ShouldBeSameAs(SqliteWriteGates.For(connectionString).Line);
        gate.Wait.ShouldBe(TimeSpan.FromSeconds(30), "the gate waits as long as the connection's own busy timeout, 30 s by default");
    }

    /// <summary>
    /// A writer behind a turn that is never handed back fails once the connection's busy timeout passes, with the busy
    /// error the engine itself would have raised — and its failed wait takes nothing: the gate still admits exactly one
    /// writer afterwards.
    /// </summary>
    [Fact]
    public async Task A_writer_stuck_behind_a_held_turn_fails_with_the_busy_error_after_the_timeout_and_leaks_nothing()
    {
        var gate = SqliteWriteGates.For($"Data Source={_databasePath};Default Timeout=1");
        var holder = await gate.EnterAsync(Ct);
        var waited = System.Diagnostics.Stopwatch.StartNew();

        var busy = await Should.ThrowAsync<SqliteException>(gate.EnterAsync(Ct).AsTask().WaitAsync(_deadline, Ct));

        busy.SqliteErrorCode.ShouldBe(5);
        waited.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9));
        holder.Dispose();
        using var next = await gate.EnterAsync(Ct).AsTask().WaitAsync(_deadline, Ct);
        var after = gate.EnterAsync(Ct).AsTask();
        after.IsCompleted.ShouldBeFalse("a leaked or doubled release would let a second writer in beside the first");
        next.Dispose();
        (await after.WaitAsync(_deadline, Ct)).Dispose();
    }

    /// <summary>A store write behind a held turn surfaces as the same busy <see cref="SqliteException"/> the busy retry used to.</summary>
    [Fact]
    public async Task A_store_write_behind_a_held_turn_fails_with_the_busy_error_callers_already_handle()
    {
        var connectionString = $"Data Source={_databasePath};Pooling=False;Default Timeout=1";
        var gate = SqliteWriteGates.For(connectionString);
        await using var world = await DeliveryStoreWorld.StartAsync(() => new SqliteConnection(connectionString), gate);
        var request = new DeliveryRequest { EventId = await world.AppendEventAsync("tickets:1"), Subscriber = Subscriber, PartitionKey = "tickets:1", Kind = "handler" };
        using var holder = await gate.EnterAsync(Ct);

        var busy = await Should.ThrowAsync<SqliteException>(world.Store.FanOutAsync([request], Ct).WaitAsync(_deadline, Ct));

        busy.SqliteErrorCode.ShouldBe(5);
    }

    /// <summary>The caller's cancellation wins over a long busy timeout, and takes nothing with it.</summary>
    [Fact]
    public async Task A_cancelled_wait_ends_before_the_busy_timeout()
    {
        var gate = SqliteWriteGates.For($"Data Source={_databasePath};Default Timeout=60");
        using var holder = await gate.EnterAsync(Ct);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancel.Token).AsTask();

        waiting.IsCompleted.ShouldBeFalse();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(waiting.WaitAsync(_deadline, Ct));
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
                RecordingWriteGate.RaiseTo(ref most, now);
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
    /// Sixty-four claimants on real threads, each claiming and then answering in its own write transaction: every claim and
    /// every answer queues at the database's gate, never more than one is inside it, none meets a busy database, and every
    /// delivery is taken once.
    /// </summary>
    /// <remarks>
    /// The gate is the database's own, wrapped to count, so the fact fails on every run if a store stops entering it — a
    /// busy-retry timeout cannot tell the two apart: without the gate the same run passes on a three-second
    /// <c>Default Timeout</c>, and fails only on one second, and then not every time. Three seconds is here so a loaded
    /// runner's reads cannot fail it.
    /// </remarks>
    [Fact]
    public async Task Sixty_four_claimants_on_threads_queue_at_the_gate_one_at_a_time_and_take_every_delivery_once()
    {
        var connectionString = $"Data Source={_databasePath};Pooling=False;Default Timeout=3";
        var gate = new RecordingWriteGate(SqliteWriteGates.For(connectionString));
        await using var world = await DeliveryStoreWorld.StartAsync(() => new SqliteConnection(connectionString), gate);
        await FanOutLanesAsync(world);
        var before = gate.Entered;

        var claimed = await Task.WhenAll(Enumerable.Range(0, Claimants).Select(index => Task.Run(() => ClaimAndAnswerAsync(world.Store, $"worker-{index}"), Ct)));

        var ids = claimed.SelectMany(batch => batch).ToList();
        ids.ShouldBeUnique();
        ids.Count.ShouldBe(Claimants);
        (gate.Entered - before).ShouldBe(Claimants * 2, "each claimant's claim and its answer wait their turn at the gate");
        gate.MostInside.ShouldBe(1);
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
