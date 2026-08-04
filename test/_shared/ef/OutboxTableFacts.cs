using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

using Xunit;

// EF1001 matches on the namespace ending in ".Internal", so here it flags Alvo's OWN internals rather than an
// Entity Framework internal API — both driver test projects are granted them by InternalsVisibleTo. A
// file-scoped suppression rather than a project-wide NoWarn, which would also hide a genuine EF-internal use.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// What <see cref="OutboxTable"/> does against a real engine: the round trip through the production writer and
/// reader, the rollback that must leave nothing behind, the stored timestamp form, and the repeatable DDL.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both engine test projects rather than copied, for the same reason
/// <see cref="DifferentialProbe"/> is: every claim here is engine-agnostic by construction — the DDL is one
/// <c>CREATE TABLE IF NOT EXISTS</c> over ANSI-portable types and the insert is one parameterised
/// <c>INSERT</c> — so two per-engine copies of the facts would be two chances for the engines to stop being
/// asked the same question. Everything engine-specific arrives through <see cref="CreateConnection"/>.
/// </para>
/// <para>
/// Each test gets its own empty database (the derived class opens a fresh one per instance, and xUnit builds
/// one instance per test), so <c>ShouldBe(0)</c> below really means "this insert left nothing" rather than
/// "some earlier test happened not to write".
/// </para>
/// </remarks>
public abstract class OutboxTableFacts
{
    /// <summary>A closed connection to this test's own empty database.</summary>
    /// <remarks>Opened by <see cref="OutboxWorld"/>, which owns and disposes it from then on.</remarks>
    protected abstract DbConnection CreateConnection();

    /// <summary>Skips the test when the engine is unavailable on this runner. A no-op for an in-process engine.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>
    /// The envelope the write path stores is the envelope the dispatcher reads back — through
    /// <see cref="AlvoEventJson"/> both ways, and through the real column, so a storage type that mangled the
    /// JSON would fail here rather than in the dispatcher.
    /// </summary>
    [Fact]
    public async Task An_inserted_event_round_trips_through_the_production_writer_and_reader()
    {
        EnsureEngineAvailable();
        await using var world = await StartAsync();
        var @event = SampleEvent();

        await world.InsertAsync(@event);

        var stored = await world.ReadPayloadAsync(@event.Id);
        AlvoEventJson.Read(stored).ShouldBe(@event);
    }

    /// <summary>
    /// The insert rides the <b>caller's</b> transaction, so a rollback leaves no event. This is the whole point
    /// of the seam (<c>docs/architecture/data-path.md</c>) and it is asserted here, before any write site emits,
    /// because an insert on its own connection would pass every other fact in this class.
    /// </summary>
    [Fact]
    public async Task An_insert_on_a_rolled_back_transaction_leaves_no_row()
    {
        EnsureEngineAvailable();
        await using var world = await StartAsync();

        await world.InsertAndRollBackAsync(SampleEvent());

        (await world.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A row's <c>created_at</c> is the framework's own round-trippable text form, from
    /// <see cref="StoredInstant"/> — the same rendering every other framework bookkeeping table stores, so no
    /// second copy of the conversion can disagree with it.
    /// </summary>
    [Fact]
    public async Task Every_timestamp_is_stored_in_the_frameworks_own_round_trip_text_form()
    {
        EnsureEngineAvailable();
        await using var world = await StartAsync();
        var @event = SampleEvent() with { Time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero) };

        await world.InsertAsync(@event);

        (await world.ReadCreatedAtTextAsync(@event.Id)).ShouldBe("2026-08-03T09:30:00.0000000+00:00");
    }

    /// <summary>
    /// The DDL is safe to run on every boot: two creators reach it (the initializer on apply and the write path
    /// on demand), so a second call must be a no-op rather than an error or a truncation.
    /// </summary>
    [Fact]
    public async Task A_second_ensure_is_a_no_op_so_the_ddl_is_safe_to_run_on_every_boot()
    {
        EnsureEngineAvailable();
        await using var world = await StartAsync();

        await world.InsertAsync(SampleEvent());
        await world.EnsureAsync();
        await world.EnsureAsync();

        (await world.CountAsync()).ShouldBe(
            1, "a repeated CREATE TABLE IF NOT EXISTS must not replace the table it found");
    }

    /// <summary>
    /// The ordering key really orders: ids minted by <see cref="AlvoEventId"/> come back from the engine in the
    /// order they were minted, under <c>ORDER BY id</c> over the stored <c>TEXT</c>.
    /// </summary>
    /// <remarks>
    /// The rows are inserted in <em>reverse</em> mint order, so an engine that answered in insertion order —
    /// which both do when nothing sorts them — would fail this rather than pass it by accident. That the
    /// engine's collation agrees with .NET's ordinal sort is spike Q2; that the mint is monotonic within one
    /// process is Q1. This fact is where the two meet on a real column.
    /// </remarks>
    [Fact]
    public async Task The_engine_orders_minted_ids_the_way_they_were_minted()
    {
        EnsureEngineAvailable();
        await using var world = await StartAsync();
        var minted = MintedIdsInOneMillisecond();

        foreach (var id in minted.Reverse())
        {
            await world.InsertAsync(SampleEvent() with { Id = id });
        }

        (await world.ReadIdsInClaimOrderAsync()).ShouldBe(minted);
    }

    private const int SameMillisecondIdCount = 32;

    private static IReadOnlyList<Guid> MintedIdsInOneMillisecond()
    {
        var instant = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

        return [.. Enumerable.Range(0, SameMillisecondIdCount).Select(_ => AlvoEventId.Create(instant))];
    }

    private async Task<OutboxWorld> StartAsync() => await OutboxWorld.StartAsync(CreateConnection());

    private static AlvoEvent SampleEvent()
    {
        var rowId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
        var time = new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero);

        return new AlvoEvent
        {
            Id = AlvoEventId.Create(time),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.vehicles.updated",
            Time = time,
            Subject = $"vehicles/{rowId}",
            PartitionKey = $"vehicles:{rowId}",
            AuthType = AlvoEventAuthType.ApiKey,
            AuthId = "key-42",
            CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
            Data = new AlvoEventData
            {
                Record = Record(("status", "approved"), ("make", "vw")),
                OldRecord = Record(("status", "draft"), ("make", "vw")),
                Changed = ["status"],
            },
        };
    }

    /// <summary>
    /// Text values only, deliberately: <see cref="AlvoEventJson.Read"/> returns JSON's view of a row rather
    /// than the row's own CLR types, so a <see cref="Guid"/> or a <see cref="decimal"/> here would make the
    /// round-trip fact fail for a reason that has nothing to do with storage. The typed-value rules are pinned
    /// where they belong, on <c>AlvoEventJson</c>'s own facts.
    /// </summary>
    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal));
}

/// <summary>
/// One open connection, the <c>alvo</c> prefix, and the handful of reads the facts above need — the same shape
/// the descriptor-version store's per-engine tests already use, kept beside them so neither engine's leg grows
/// its own helper.
/// </summary>
/// <remarks>
/// Every read here is spelled out rather than taken from <see cref="OutboxTable"/>: a fact that read the row
/// back through the same statement that wrote it would pass on any column names the writer happened to use.
/// </remarks>
internal sealed class OutboxWorld : IAsyncDisposable
{
    private const string SchemaPrefix = "alvo";

    private readonly DbConnection _connection;
    private readonly string _tableName = OutboxTable.NameFor(SchemaPrefix);

    private OutboxWorld(DbConnection connection) => _connection = connection;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<OutboxWorld> StartAsync(DbConnection connection)
    {
        await connection.OpenAsync(Ct);
        var world = new OutboxWorld(connection);
        await world.EnsureAsync();

        return world;
    }

    internal Task EnsureAsync() => OutboxTable.EnsureAsync(_connection, _tableName, Ct);

    internal Task InsertAsync(AlvoEvent @event) => WriteAsync(@event, commit: true);

    internal Task InsertAndRollBackAsync(AlvoEvent @event) => WriteAsync(@event, commit: false);

    private async Task WriteAsync(AlvoEvent @event, bool commit)
    {
        var transaction = await _connection.BeginTransactionAsync(Ct);
        await using (transaction.ConfigureAwait(false))
        {
            await OutboxTable.InsertAsync(_connection, transaction, _tableName, @event, Ct);

            if (commit)
            {
                await transaction.CommitAsync(Ct);
            }
            else
            {
                await transaction.RollbackAsync(Ct);
            }
        }
    }

    internal Task<string> ReadPayloadAsync(Guid id) => ReadTextAsync("payload", id);

    internal Task<string> ReadCreatedAtTextAsync(Guid id) => ReadTextAsync("created_at", id);

    internal async Task<int> CountAsync() =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM {_tableName}"), CultureInfo.InvariantCulture);

    /// <summary>Every stored id in the order the dispatcher's claim takes them — <c>ORDER BY id</c>.</summary>
    internal async Task<IReadOnlyList<Guid>> ReadIdsInClaimOrderAsync()
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT id FROM {_tableName} ORDER BY id";

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
    }

    private async Task<string> ReadTextAsync(string column, Guid id)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT {column} FROM {_tableName} WHERE id = @id";
            RelationalSqlBatch.AddParameter(command, "@id", id.ToString());

            var value = await command.ExecuteScalarAsync(Ct);

            return value as string
                ?? throw new InvalidOperationException($"No outbox row for '{id}' carries a '{column}'.");
        }
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;

            return await command.ExecuteScalarAsync(Ct);
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
