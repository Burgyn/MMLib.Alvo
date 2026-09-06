using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Testing.Data;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The whole <see cref="AlvoDataReachabilityContractTests"/> suite against a real PostgreSQL server — the
/// engine leg SQLite cannot supply, because a refused TCP connection and a file that cannot be created are
/// different failures behind one answer.
/// </summary>
/// <remarks>
/// <para>
/// The server is shared for the whole class via <see cref="PostgresFixture"/>; the <em>database</em> is fresh
/// per instance, mirroring <see cref="PostgreSqlOutboxTableTests"/>'s isolation.
/// </para>
/// <para>
/// <b>The fresh database is for consistency with every other class here, not as a fix for anything.</b> A
/// reachability probe touches no table, so an earlier version of this file reused the fixture's own
/// connection string and said so — which was true about table state and silent about connection state. The
/// private database is the shape the rest of the project already has, so it is the cheaper default even
/// where the reasoning for it is weaker.
/// </para>
/// <para>
/// <b>What it is <em>not</em> is the answer to a local flake, and that is worth recording so nobody repeats
/// the hunt.</b> On one developer machine, adding this class made <em>other</em> classes in this project fail
/// intermittently — roughly ten failures across nineteen full runs, a different victim each time, all of them
/// Npgsql connection failures (<c>Received unknown response Z for SSLRequest</c>, 18-second
/// <c>ConnectAsync</c> timeouts) — against nine clean runs of the same project on <c>main</c>, four of them
/// under identical load. Three hypotheses were tested and all three were wrong: excluding the cancelled-probe
/// fact, giving this class a private database, and excluding the port-1 connection each left the failures in
/// place. What settled it: <b>CI runs this project green</b> (247 tests, 0 failed, 3 m 09 s on
/// <c>ubuntu-latest</c>) with this class present. The machine that flaked has Docker limited to 8 GB shared
/// with five long-running unrelated containers, so four more facts doing real TCP work tipped an
/// already-starved daemon. Treat a local failure in this project as an environment signal and check CI before
/// changing a test.
/// </para>
/// <para>
/// Everything is wired through the public <c>UsePostgreSql</c> entry point, so a probe the driver failed to
/// register fails on resolution rather than passing silently.
/// </para>
/// </remarks>
public sealed class PostgreSqlReachabilityTests : AlvoDataReachabilityContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _connectionString = string.Empty;
    private readonly List<ServiceProvider> _containers = [];

    public PostgreSqlReachabilityTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (a Windows-container runner cannot run the Linux
            // postgres:16-alpine image), so every fact skips on EnsureEngineAvailable() below before it ever
            // asks for a connection.
            return;
        }

        var databaseName = $"alvo_reach_{Guid.NewGuid():N}";
        CreateDatabase(fixture.ConnectionString, databaseName);
        _connectionString = WithDatabase(fixture.ConnectionString, databaseName);
    }

    /// <inheritdoc/>
    protected override IServiceProvider CreateReachable() => Container(_connectionString);

    /// <summary>
    /// The same server on a port nothing is bound to, refused quickly rather than waited out. Only the port
    /// moves: a fact that changed the host name too would pass for a DNS failure, which is a different
    /// diagnosis.
    /// </summary>
    /// <inheritdoc/>
    protected override IServiceProvider CreateUnreachable() => Container(
        new NpgsqlConnectionStringBuilder(_connectionString) { Port = 1, Timeout = 2 }.ConnectionString);

    /// <inheritdoc/>
    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var container in _containers)
        {
            container.Dispose();
        }
    }

    private static void CreateDatabase(string adminConnectionString, string databaseName)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    private static string WithDatabase(string connectionString, string databaseName) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;

    /// <summary>The container a host gets from <c>UsePostgreSql</c> alone, owned by this fixture.</summary>
    /// <param name="connectionString">The store this container's probe asks.</param>
    private ServiceProvider Container(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UsePostgreSql(connectionString));

        var container = collection.BuildServiceProvider();
        _containers.Add(container);

        return container;
    }
}
