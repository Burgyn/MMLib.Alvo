using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// #133's port over a real engine: the whole <see cref="AlvoDataReachabilityContractTests"/> suite plus the
/// wiring facts, over SQLite — where "unreachable" is a file that cannot be created.
/// </summary>
/// <remarks>
/// The port's implementation is shared by every EF driver
/// (<c>AlvoEfCoreProvider.AddRelationalProvider</c> registers one), so the engine here is the cheap one that
/// needs no container; the PostgreSQL leg runs the same suite where "unreachable" is a refused TCP connection.
/// Everything is wired through the public <c>UseSqlite</c> entry point, so a probe the driver failed to
/// register fails on resolution rather than passing silently.
/// </remarks>
public class SqliteReachabilityTests : AlvoDataReachabilityContractTests, IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("alvo-reach-");
    private readonly List<ServiceProvider> _containers = [];

    /// <inheritdoc/>
    protected override IAlvoDataReachability CreateReachable() =>
        Probe($"Data Source={Path.Combine(_directory.FullName, "alvo.db")}");

    /// <summary>
    /// A database under a directory that does not exist: SQLite creates a missing <em>file</em> but not a
    /// missing directory, so opening it fails with the driver's own exception.
    /// </summary>
    /// <inheritdoc/>
    protected override IAlvoDataReachability CreateUnreachable() =>
        Probe($"Data Source={Path.Combine(_directory.FullName, "no-such-directory", "alvo.db")}");

    /// <summary>The driver's public entry point alone yields a resolvable probe, as it does a data port.</summary>
    [Fact]
    public void The_public_entry_point_alone_yields_a_resolvable_reachability_port() =>
        CreateReachable().ShouldNotBeNull();

    /// <summary>
    /// A host that registered its own probe keeps it — <c>TryAdd</c> means the driver supplies a default, not
    /// an override, exactly as it does for the dialect.
    /// </summary>
    [Fact]
    public void A_host_supplied_probe_wins_over_the_drivers_default()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAlvoDataReachability>(new AlwaysReachable());
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));

        collection.Count(service => service.ServiceType == typeof(IAlvoDataReachability)).ShouldBe(1);
    }

    /// <summary>
    /// SQLite inherits the dialect's default probe statement, which is the ANSI one — read through the
    /// interface, because a default interface member is not a member of the implementing class.
    /// </summary>
    [Fact]
    public void The_dialects_probe_statement_is_a_bare_select() =>
        ((IAlvoSqlDialect)new SqliteSqlDialect()).ReachabilityProbeStatement.ShouldBe("SELECT 1");

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var container in _containers)
        {
            container.Dispose();
        }

        _directory.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class AlwaysReachable : IAlvoDataReachability
    {
        public ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AlvoReachability.Reachable);
    }

    /// <summary>The probe a host gets from <c>UseSqlite</c> alone, over one owned container.</summary>
    /// <param name="connectionString">The store this probe asks.</param>
    private IAlvoDataReachability Probe(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite(connectionString));

        var container = collection.BuildServiceProvider();
        _containers.Add(container);

        return container.GetRequiredService<IAlvoDataReachability>();
    }
}
