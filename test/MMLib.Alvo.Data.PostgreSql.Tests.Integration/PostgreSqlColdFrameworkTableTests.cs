using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

using Npgsql;

using Shouldly;

using System.Globalization;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// First keyed writes against a database whose idempotency table another process is creating at that very moment: every
/// one of them succeeds, because "someone else created it" is the outcome <c>CREATE TABLE IF NOT EXISTS</c> asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is PostgreSQL's fact.</b> <c>CREATE TABLE IF NOT EXISTS</c> is not atomic there: a session that passed the
/// existence check while another session's create of the same name was uncommitted blocks on that session's
/// <c>pg_type</c> row and, once it commits, fails with <c>23505 … "pg_type_typname_nsp_index"</c> rather than skipping.
/// A keyed write ensures the idempotency table on a connection of its own before any transaction (the stage-0 lookup),
/// outside the retry loop, so the lost race used to fail the caller's write — found by CI on
/// <c>PostgreSqlAlvoDataOutboxTests.Two_concurrent_idempotent_creates_on_one_key_emit_exactly_one_event</c>.
/// </para>
/// <para>
/// <b>Deterministic, not a timing lottery.</b> The rival process is played by this fact: it creates the table inside a
/// transaction it holds open, waits until every replica's own create is blocked on it — observed in
/// <c>pg_stat_activity</c>, not guessed with a sleep — and only then commits. Every replica therefore loses the race,
/// every time. Two replicas with two concurrent writes each cover both halves: within a process the first ensure is
/// shared, across processes the loser has to recognise the winner's table.
/// </para>
/// </remarks>
public sealed class PostgreSqlColdFrameworkTableTests : IAsyncLifetime
{
    private const int Replicas = 2;
    private const int WritesPerReplica = 2;
    private const string Notes = "notes";

    /// <summary>
    /// The idempotency table's shape, restated because the driver's DDL is internal to it. Only the name has to match for
    /// the race; the columns match too, so the writes that follow the race succeed against the rival's table.
    /// </summary>
    private const string RivalCreate =
        "CREATE TABLE alvo_idempotency (idempotency_key TEXT NOT NULL, scope TEXT NOT NULL, fingerprint TEXT NOT NULL, "
        + "row_id TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (idempotency_key, scope))";

    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task First_keyed_writes_racing_another_process_creating_the_idempotency_table_all_succeed()
    {
        var (hosts, connectionString) = await _fixture.StartReplicasAsync(Schema, Descriptor, Replicas);
        await using var rival = new NpgsqlConnection(connectionString);
        await rival.OpenAsync(Ct);
        await using var creating = await rival.BeginTransactionAsync(Ct);
        await ExecuteAsync(rival, RivalCreate);

        var writes = hosts
            .SelectMany((host, replica) => Enumerable.Range(0, WritesPerReplica).Select(write => KeyedCreateAsync(host, $"r{replica}-w{write}")))
            .ToArray();
        await BlockedOnTheRivalAsync(connectionString, Replicas, writes);
        await creating.CommitAsync(Ct);
        await Task.WhenAll(writes);

        var all = await hosts[0].Data.QueryAsync(new AlvoQuery { Entity = Notes }, Caller, Ct);
        all.Items.Count.ShouldBe(Replicas * WritesPerReplica, "every first keyed write must land despite the lost race");
    }

    /// <summary>
    /// Waits until at least <paramref name="expected"/> sessions of the database are waiting on a lock — the replicas'
    /// creates, blocked behind the rival's uncommitted one. A write that already failed ends the wait early, so a broken
    /// fact reports its own exception rather than a timeout.
    /// </summary>
    /// <remarks>
    /// Observed on a connection of its own, in autocommit: a backend caches <c>pg_stat_activity</c> for the rest of the
    /// transaction that first read it, so the rival — which holds its transaction open — would watch a frozen view.
    /// </remarks>
    private static async Task BlockedOnTheRivalAsync(string connectionString, int expected, Task[] writes)
    {
        const string Waiting =
            "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";

        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(Ct);
        while (Convert.ToInt32(await ScalarAsync(observer, Waiting), CultureInfo.InvariantCulture) < expected)
        {
            if (writes.FirstOrDefault(write => write.IsFaulted) is { } failed)
            {
                await failed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    private static Task<AlvoRecord> KeyedCreateAsync(PostgreSqlAlvoDataHost host, string key) =>
        host.Data.CreateAsync(
            Notes,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = key },
            Caller,
            new AlvoIdempotency(key, $"{Notes}:{key}"),
            Ct);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "cold-framework-tables",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Notes] = new()
            {
                Tenancy = EntityTenancy.Global,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.String },
                },
                Rules = new AccessRules { List = "true", Get = "true", Create = "true" },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Notes,
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = MMLib.Alvo.Schema.FieldType.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = MMLib.Alvo.Schema.FieldType.String, Nullable = true },
            ],
        },
    ]);
}
