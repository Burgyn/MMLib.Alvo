using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Data;
using Npgsql;
using NpgsqlTypes;
using System.Data.Common;
using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>
/// Real PostgreSQL for the API suite: one container for the test class that owns the engine, and a fresh
/// database per <see cref="AlvoApiWorld"/>.
/// </summary>
/// <remarks>
/// <para>
/// A container per fact would be prohibitively slow; a shared database would break the suite's per-fact
/// isolation, since several facts assert exact row counts over a whole table. That is the same trade
/// <c>PostgreSqlAlvoDataFixture</c> makes for the port-level suites. The container-creation mechanics live in
/// <see cref="PostgresTestContainer"/>, shared with <c>PostgresFixture</c> — a second copy here is exactly
/// how the two would quietly drift apart.
/// </para>
/// <para>
/// <b>The container is built inside <see cref="InitializeAsync"/>, never in a field initializer.</b>
/// <see cref="PostgresTestContainer.BuildAndStartAsync"/> itself talks to the Docker daemon, so on a host
/// with no reachable daemon it throws while the fixture is being <em>constructed</em> — which xUnit reports
/// as every test in the sharing class failing, before any of them reaches its own skip. PR1 lost 28 tests to
/// exactly that on a Windows runner. Do not reintroduce it.
/// </para>
/// <para>
/// <b>Any failure to reach the daemon leaves this engine unavailable, not only Windows's.</b> Windows GitHub
/// runners run Docker in Windows-container mode, which has no linux/amd64 manifest for
/// <c>postgres:16-alpine</c> — but a Linux or macOS host can just as well have no daemon running at all, and
/// both are the same condition from a caller's point of view: <see cref="Available"/> is false and every
/// fact self-skips through <see cref="CreateDatabaseAsync"/>, instead of the class failing outright on the
/// platform this used to check for by name.
/// </para>
/// </remarks>
public sealed class PostgresApiEngine : AlvoApiEngine, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _unavailableReason;
    private readonly List<string> _connectionStrings = [];
    private readonly Lock _gate = new();

    /// <summary>Gets whether a real engine was started, so a caller can skip rather than fail.</summary>
    public bool Available => _container is not null;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = await PostgresTestContainer.BuildAndStartAsync();
        }
        catch (Exception exception)
        {
            _unavailableReason = exception.Message;
        }
    }

    /// <inheritdoc/>
    public override async Task<AlvoApiDatabase> CreateDatabaseAsync()
    {
        Assert.SkipUnless(
            Available,
            $"No reachable Docker daemon, so the PostgreSQL engine could not be started: {_unavailableReason}.");

        var admin = _container!.GetConnectionString();
        var name = $"alvo_api_{Guid.NewGuid():N}";
        await CreateAsync(admin, name);

        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ToString();
        lock (_gate)
        {
            _connectionStrings.Add(connectionString);
        }

        return new PostgresApiDatabase(connectionString, name);
    }

    /// <summary>
    /// Creates the database off the container's own admin connection. The name is a <see cref="Guid"/>, so it
    /// cannot collide, and it doubles as the marker that identifies this database's statements.
    /// </summary>
    private static async Task CreateAsync(string adminConnectionString, string name)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{name}\"";
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The pools are cleared before the container goes, not because the databases need dropping — the
    /// container takes them with it — but because Npgsql keeps idle physical connections per connection
    /// string, and a class that started a dozen worlds would otherwise leave a dozen pools holding sockets to
    /// a container that no longer exists.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        List<string> connectionStrings;
        lock (_gate)
        {
            connectionStrings = [.. _connectionStrings];
            _connectionStrings.Clear();
        }

        foreach (var connectionString in connectionStrings)
        {
            await using var pooled = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(pooled);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>One database inside the shared container, for the lifetime of one <see cref="AlvoApiWorld"/>.</summary>
/// <param name="connectionString">The connection string addressing it.</param>
/// <param name="name">Its generated name, which is also its statement marker.</param>
public sealed class PostgresApiDatabase(string connectionString, string name) : AlvoApiDatabase
{
    /// <inheritdoc/>
    public override string Marker => name;

    /// <inheritdoc/>
    public override void Use(IAlvoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UsePostgreSql(connectionString);
    }

    /// <inheritdoc/>
    public override DbConnection Connect() => new NpgsqlConnection(connectionString);

    /// <summary>
    /// Bulk-loads <paramref name="rowCount"/> vehicles owned by <paramref name="owner"/> over Npgsql's binary
    /// <c>COPY</c> protocol — the one place this suite's seeding speaks PostgreSQL directly rather than a
    /// portable <c>DbCommand</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw connection <c>COPY</c> needs never leaves this method. Earlier this was a public
    /// <c>ConnectToPostgres()</c> handing out a policy-free <see cref="NpgsqlConnection"/> to any fact in the
    /// assembly; only the seeding operation itself is exposed now, so no future fact can reach a raw writer
    /// for anything else.
    /// </para>
    /// <para>
    /// Binary rather than text: every value crosses as its CLR type, so no <see cref="Guid"/>, timestamp or
    /// integer is ever formatted into text and re-parsed — which is where a hand-rolled seed diverges from
    /// what the production write path stores. The nullable managed columns (<c>created_by</c>,
    /// <c>updated_by</c>) and the nullable <c>color</c> are left out of the column list rather than written
    /// as null, so the table itself supplies them.
    /// </para>
    /// <para>
    /// <b>It stays reachable from every fact in this assembly, and that is a decision rather than an
    /// oversight.</b> A review round asked for the reach to be narrowed to the seeding type alone; C# cannot
    /// express that inside one assembly (<c>internal</c> is this assembly), and the one move that would —
    /// lifting the seeder into its own type — needs <c>connectionString</c> exposed, which re-creates exactly
    /// the raw policy-free writer the same round removed. Trading a bounded seeder for an unbounded connection
    /// is the worse half of that trade. What is actually bounded is the <em>capability</em>: this is a
    /// <c>COPY</c> into one fixed column list of one table, not a route around a policy predicate, and it must
    /// not grow into one — a general-purpose writer here would be a way to stage rows no rule ever judged.
    /// </para>
    /// </remarks>
    /// <param name="owner">The owner every seeded vehicle references.</param>
    /// <param name="rowCount">How many vehicles to load.</param>
    /// <param name="make">The <c>make</c> for a given row index.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task<List<Guid>> CopyVehiclesAsync(
        Guid owner, int rowCount, Func<int, string> make, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>(rowCount);
        var stamped = DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY vehicles (id, created_at, updated_at, vin, plate, make, model, year, owner_id) "
            + "FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        foreach (var index in Enumerable.Range(0, rowCount))
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await WriteVehicleAsync(writer, id, owner, index, make(index), stamped, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
        return ids;
    }

    private static async Task WriteVehicleAsync(
        NpgsqlBinaryImporter writer,
        Guid id,
        Guid owner,
        int index,
        string make,
        DateTimeOffset stamped,
        CancellationToken cancellationToken)
    {
        await writer.StartRowAsync(cancellationToken);
        await writer.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
        await writer.WriteAsync(stamped, NpgsqlDbType.TimestampTz, cancellationToken);
        await writer.WriteAsync(stamped, NpgsqlDbType.TimestampTz, cancellationToken);
        await writer.WriteAsync($"VIN{index:D14}", NpgsqlDbType.Varchar, cancellationToken);
        await writer.WriteAsync($"S-{index:D9}", NpgsqlDbType.Varchar, cancellationToken);
        await writer.WriteAsync(make, NpgsqlDbType.Varchar, cancellationToken);
        await writer.WriteAsync("model", NpgsqlDbType.Varchar, cancellationToken);

        // An `integer` descriptor field maps to a PostgreSQL `bigint`, so the operand is a long — a mismatch
        // here is a COPY-time failure rather than a silent conversion.
        await writer.WriteAsync((long)(1990 + (index % 30)), NpgsqlDbType.Bigint, cancellationToken);
        await writer.WriteAsync(owner, NpgsqlDbType.Uuid, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to release per database: the databases live and die with the container, and dropping one here
    /// would have to terminate its own pooled connections first — cost with no benefit inside a container
    /// that is about to be thrown away. The engine clears the pools when it disposes.
    /// </remarks>
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
