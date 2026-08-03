using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

using System.Data.Common;
using System.Globalization;

using Xunit;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — both driver
// test projects are granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// One <see cref="EfCoreOutboxStore"/> over a started database, plus the seeding and the clock
/// <see cref="OutboxStoreContractTests"/> asks for — and the two-claimant race only PostgreSQL's leg runs.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both driver test projects rather than copied, for the reason <see cref="DifferentialProbe"/>
/// is: the claim protocol is engine-agnostic by construction, so two per-engine copies of the seeding and the
/// clock are two chances for the engines to stop being asked the same question. Everything engine-specific
/// arrives as the connection factory.
/// </para>
/// <para>
/// Seeding goes through <see cref="OutboxTable.InsertAsync"/> — the production writer, on a real transaction —
/// so a queue this suite claims from is a queue the write path could have produced. Ids come from
/// <see cref="AlvoEventId"/> for the same reason: a test that minted its own would be free to mint them in an
/// order the shipped generator never produces.
/// </para>
/// </remarks>
internal sealed class OutboxStoreWorld : IOutboxStoreWorld
{
    private const string SchemaPrefix = "alvo";

    /// <summary>
    /// How long the loser of the two-claimant race is given to prove it is blocked rather than finished.
    /// </summary>
    private static readonly TimeSpan _blockObservationWindow = TimeSpan.FromMilliseconds(500);

    private readonly Func<DbConnection> _createConnection;
    private readonly AdvanceableClock _clock;
    private readonly string _tableName = OutboxTable.NameFor(SchemaPrefix);

    private OutboxStoreWorld(Func<DbConnection> createConnection, AdvanceableClock clock)
    {
        _createConnection = createConnection;
        _clock = clock;
        Store = new EfCoreOutboxStore(
            new RelationalConnectionFactory(createConnection),
            new AlvoOptions { SchemaPrefix = SchemaPrefix },
            clock);
    }

    /// <inheritdoc/>
    public IOutboxStore Store { get; }

    /// <summary>Builds a world over an empty queue, with the table already created.</summary>
    /// <param name="createConnection">Creates a fresh, unopened connection to this test's own database.</param>
    internal static async Task<OutboxStoreWorld> StartAsync(Func<DbConnection> createConnection)
    {
        var world = new OutboxStoreWorld(createConnection, new AdvanceableClock(_seedInstant));
        await world.Store.EnsureAsync(Ct);

        return world;
    }

    /// <inheritdoc/>
    public void Advance(TimeSpan duration) => _clock.Advance(duration);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> SeedAsync(int count)
    {
        var ids = new List<Guid>(count);
        for (var index = 0; index < count; index++)
        {
            ids.Add(await AppendAsync(AlvoEventId.Create(_clock.GetUtcNow())));
        }

        return ids;
    }

    /// <inheritdoc/>
    public Task<Guid> SeedWithExplicitIdAsync(Guid id) => AppendAsync(id);

    /// <summary>
    /// Two claimants racing one queue with no <c>SKIP LOCKED</c>: A claims and holds its transaction open, B's
    /// claim blocks on A's row locks, A commits, and B is then awaited.
    /// </summary>
    /// <param name="batchSize">The batch each claimant asks for.</param>
    /// <returns>What A claimed, and what B claimed once it unblocked.</returns>
    /// <remarks>
    /// <para>
    /// Two raw connections with explicit transactions rather than two <see cref="EfCoreOutboxStore"/> calls,
    /// because the store issues one autocommit statement per call and therefore leaves no window a test can
    /// hold open — the race has to be constructed, or the fact is a coin toss. The statement itself is the
    /// production one, <see cref="OutboxTable.ClaimSql"/>, so this measures what ships.
    /// </para>
    /// <para>
    /// B is asserted to still be running before A commits. Without that guard a run in which B never reached
    /// the lock at all would look like a pass, which is exactly how this fact would stop testing anything.
    /// </para>
    /// </remarks>
    internal async Task<(IReadOnlyList<Guid> First, IReadOnlyList<Guid> Second)> TwoConcurrentClaimsAsync(
        int batchSize)
    {
        var winner = _createConnection();
        await using (winner.ConfigureAwait(false))
        {
            var loser = _createConnection();
            await using (loser.ConfigureAwait(false))
            {
                await winner.OpenAsync(Ct);
                await loser.OpenAsync(Ct);

                return await RaceAsync(winner, loser, batchSize);
            }
        }
    }

    /// <summary>The highest attempt count in the queue — a double claim increments it whoever sees the rows.</summary>
    internal async Task<int> MaxAttemptsAsync()
    {
        var connection = _createConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"SELECT MAX(attempts) FROM {_tableName}";
                var value = await command.ExecuteScalarAsync(Ct);

                return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(IReadOnlyList<Guid> First, IReadOnlyList<Guid> Second)> RaceAsync(
        DbConnection winner, DbConnection loser, int batchSize)
    {
        var winning = await winner.BeginTransactionAsync(Ct);
        await using (winning.ConfigureAwait(false))
        {
            var claimedByWinner = await ClaimOnAsync(winner, winning, batchSize);

            var losing = await loser.BeginTransactionAsync(Ct);
            await using (losing.ConfigureAwait(false))
            {
                var blocked = ClaimOnAsync(loser, losing, batchSize);
                await Task.Delay(_blockObservationWindow, Ct);
                ThrowIfNeverBlocked(blocked);

                await winning.CommitAsync(Ct);
                var claimedByLoser = await blocked;
                await losing.CommitAsync(Ct);

                return (claimedByWinner, claimedByLoser);
            }
        }
    }

    private static void ThrowIfNeverBlocked(Task<IReadOnlyList<Guid>> loser)
    {
        if (loser.IsCompleted)
        {
            throw new InvalidOperationException(
                "The second claimant finished before the first committed, so no race was constructed and "
                + "this fact would pass whatever the statement said.");
        }
    }

    private async Task<IReadOnlyList<Guid>> ClaimOnAsync(
        DbConnection connection, DbTransaction transaction, int batchSize)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = OutboxTable.ClaimSql(_tableName);
            RelationalSqlBatch.AddParameter(command, "@claimed_at", StoredInstant.Text(_clock.GetUtcNow()));
            RelationalSqlBatch.AddParameter(command, "@claimed_by", "racer");
            RelationalSqlBatch.AddParameter(
                command, "@stale_before", StoredInstant.Text(_clock.GetUtcNow() - _raceLease));
            RelationalSqlBatch.AddParameter(command, "@max_attempts", RaceMaxAttempts);
            RelationalSqlBatch.AddParameter(command, "@batch", batchSize);

            return await ReadIdsAsync(command);
        }
    }

    private static async Task<IReadOnlyList<Guid>> ReadIdsAsync(DbCommand command)
    {
        var reader = await command.ExecuteReaderAsync(Ct);
        await using (reader.ConfigureAwait(false))
        {
            var ids = new List<Guid>();
            while (await reader.ReadAsync(Ct))
            {
                ids.Add(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
            }

            return ids;
        }
    }

    private async Task<Guid> AppendAsync(Guid id)
    {
        var connection = _createConnection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            var transaction = await connection.BeginTransactionAsync(Ct);
            await using (transaction.ConfigureAwait(false))
            {
                await OutboxTable.InsertAsync(connection, transaction, _tableName, EventWith(id), Ct);
                await transaction.CommitAsync(Ct);
            }
        }

        return id;
    }

    private AlvoEvent EventWith(Guid id) => new()
    {
        Id = id,
        Source = AlvoEvent.DefaultSource,
        Type = "entity.vehicles.created",
        Time = _clock.GetUtcNow(),
        Subject = $"vehicles/{id}",
        PartitionKey = $"vehicles:{id}",
        AuthType = AlvoEventAuthType.ApiKey,
        AuthId = "key-42",
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = new(new Dictionary<string, object?>(StringComparer.Ordinal)) },
    };

    private const int RaceMaxAttempts = 5;

    private static readonly DateTimeOffset _seedInstant = new(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan _raceLease = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clock the test moves by hand, so a lease expires without anyone waiting for one.</summary>
    private sealed class AdvanceableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
