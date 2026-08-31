using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
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
/// The server is shared for the whole class via <see cref="PostgresFixture"/>; nothing here creates a database
/// of its own, because a reachability probe touches no table and therefore cannot collide with another test's
/// state. Wired through the public <c>UsePostgreSql</c> entry point, so a probe the driver failed to register
/// fails on resolution rather than passing silently.
/// </remarks>
public sealed class PostgreSqlReachabilityTests : AlvoDataReachabilityContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _containers = [];

    public PostgreSqlReachabilityTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _connectionString = fixture.ConnectionString;
    }

    /// <inheritdoc/>
    protected override IAlvoDataReachability CreateReachable() => Probe(_connectionString);

    /// <summary>
    /// The same server on a port nothing is bound to, refused quickly rather than waited out. Only the port
    /// moves: a fact that changed the host name too would pass for a DNS failure, which is a different
    /// diagnosis.
    /// </summary>
    /// <inheritdoc/>
    protected override IAlvoDataReachability CreateUnreachable() => Probe(
        new NpgsqlConnectionStringBuilder(_connectionString) { Port = 1, Timeout = 2 }.ToString());

    /// <inheritdoc/>
    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    /// <summary>PostgreSQL inherits the dialect's default probe statement, which is the ANSI one.</summary>
    [Fact]
    public void The_dialects_probe_statement_is_a_bare_select() =>
        ((EntityFrameworkCore.IAlvoSqlDialect)new PostgreSqlSqlDialect()).ReachabilityProbeStatement
            .ShouldBe("SELECT 1");

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var container in _containers)
        {
            container.Dispose();
        }
    }

    /// <summary>The probe a host gets from <c>UsePostgreSql</c> alone, over one owned container.</summary>
    /// <param name="connectionString">The store this probe asks.</param>
    private IAlvoDataReachability Probe(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UsePostgreSql(connectionString));

        var container = collection.BuildServiceProvider();
        _containers.Add(container);

        return container.GetRequiredService<IAlvoDataReachability>();
    }
}
