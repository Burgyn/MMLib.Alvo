using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Tests;
using Npgsql;
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
/// <c>PostgreSqlAlvoDataFixture</c> makes for the port-level suites.
/// </para>
/// <para>
/// <b>The container is built inside <see cref="InitializeAsync"/>, never in a field initializer.</b>
/// <see cref="PostgreSqlBuilder.Build"/> itself talks to the Docker daemon, so on a host with no reachable
/// daemon it throws while the fixture is being <em>constructed</em> — which xUnit reports as every test in
/// the sharing class failing, before any of them reaches its own skip. PR1 lost 28 tests to exactly that on a
/// Windows runner. Do not reintroduce it.
/// </para>
/// </remarks>
public sealed class PostgresApiEngine : AlvoApiEngine, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private readonly List<string> _connectionStrings = [];
    private readonly Lock _gate = new();

    /// <summary>Gets whether a real engine was started, so a caller can skip rather than fail.</summary>
    public bool Available => _container is not null;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        // Windows GitHub runners run Docker in Windows-container mode when they run it at all, and that mode
        // has no linux/amd64 manifest for postgres:16-alpine. Every caller self-skips (see
        // CreateDatabaseAsync), so there is nothing to start; Linux stays strict, because skipping there
        // would silently drop the whole real-PostgreSQL leg of the API suite.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Explicit tag: PostgreSqlBuilder's parameterless ctor and its PostgreSqlImage constant are both
        // obsolete in Testcontainers.PostgreSql 4.13 in favour of an explicit image argument.
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        _container = container;
    }

    /// <inheritdoc/>
    public override async Task<AlvoApiDatabase> CreateDatabaseAsync()
    {
        Assert.SkipUnless(
            Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

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
    /// The same connection, typed — for the one caller that needs PostgreSQL's own bulk-load protocol
    /// (<c>COPY</c>) rather than a portable <c>DbCommand</c>.
    /// </summary>
    public NpgsqlConnection ConnectToPostgres() => new(connectionString);

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to release per database: the databases live and die with the container, and dropping one here
    /// would have to terminate its own pooled connections first — cost with no benefit inside a container
    /// that is about to be thrown away. The engine clears the pools when it disposes.
    /// </remarks>
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
