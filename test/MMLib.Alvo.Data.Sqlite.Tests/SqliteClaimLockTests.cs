using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Both claims stamp their lease from a clock read while they already hold SQLite's write lock, so a claim that waited
/// for the lock does not start its lease in the past.
/// </summary>
public sealed class SqliteClaimLockTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-claim-lock-tests-{Guid.NewGuid():N}.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_outbox_claim_reads_the_clock_under_the_write_lock()
    {
        var clock = new LockProbeClock(Connect);
        var store = new EfCoreOutboxStore(new RelationalConnectionFactory(Connect), new AlvoOptions(), clock);
        await store.EnsureAsync(Ct);
        clock.Reads.Clear();

        await store.ClaimAsync("worker", batchSize: 5, maxAttempts: 3, TimeSpan.FromMinutes(1), Ct);

        clock.Reads.ShouldNotBeEmpty();
        clock.Reads.ShouldAllBe(locked => locked);
    }

    [Fact]
    public async Task The_delivery_claim_reads_the_clock_under_the_write_lock()
    {
        var clock = new LockProbeClock(Connect);
        var store = new EfCoreDeliveryStore(new RelationalConnectionFactory(Connect), new SqliteSqlDialect(), new AlvoOptions(), clock);
        await store.EnsureAsync(Ct);
        clock.Reads.Clear();

        await store.ClaimAsync(
            new DeliveryClaim
            {
                Claimant = "worker",
                Ordered = ["send-confirmation"],
                BatchSize = 5,
                Lease = TimeSpan.FromMinutes(1),
                OutboxMaxAttempts = 3,
                OutboxClaimLease = TimeSpan.FromMinutes(5),
            },
            Ct);

        clock.Reads.ShouldNotBeEmpty();
        clock.Reads.ShouldAllBe(locked => locked);
    }

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

    /// <summary>Records, on every read, whether another connection could take the write lock at that moment.</summary>
    private sealed class LockProbeClock(Func<SqliteConnection> connect) : TimeProvider
    {
        internal List<bool> Reads { get; } = [];

        public override DateTimeOffset GetUtcNow()
        {
            Reads.Add(WriteLockIsHeld());

            return new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        }

        private bool WriteLockIsHeld()
        {
            using var probe = connect();
            probe.Open();
            using var begin = probe.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.CommandTimeout = 1;
            try
            {
                begin.ExecuteNonQuery();
            }
            catch (SqliteException busy) when (busy.SqliteErrorCode == 5)
            {
                return true;
            }

            using var rollback = probe.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();

            return false;
        }
    }
}
