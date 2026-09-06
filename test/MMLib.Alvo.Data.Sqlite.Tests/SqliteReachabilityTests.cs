using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// #133's port over a real engine: the whole <see cref="AlvoDataReachabilityContractTests"/> suite plus the
/// wiring fact, over SQLite — where "unreachable" is a file that cannot be created.
/// </summary>
/// <remarks>
/// The port's implementation is shared by every EF driver
/// (<c>AlvoEfCoreProvider.AddRelationalProvider</c> registers one), so the engine here is the cheap one that
/// needs no container; the PostgreSQL leg runs the same suite where "unreachable" is a refused TCP
/// connection. Everything is wired through the public <c>UseSqlite</c> entry point, so a probe the driver
/// failed to register fails on resolution rather than passing silently.
/// </remarks>
public class SqliteReachabilityTests : AlvoDataReachabilityContractTests, IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("alvo-reach-");
    private readonly List<ServiceProvider> _containers = [];

    /// <inheritdoc/>
    protected override IServiceProvider CreateReachable() =>
        Container($"Data Source={Path.Combine(_directory.FullName, "alvo.db")}");

    /// <summary>
    /// A database under a directory that does not exist: SQLite creates a missing <em>file</em> but not a
    /// missing directory, so opening it fails with the driver's own exception.
    /// </summary>
    /// <inheritdoc/>
    protected override IServiceProvider CreateUnreachable() =>
        Container($"Data Source={Path.Combine(_directory.FullName, "no-such-directory", "alvo.db")}");

    /// <summary>
    /// A host that registered its own probe keeps it — <c>TryAdd</c> means the driver supplies a default, not
    /// an override, exactly as it does for the dialect.
    /// </summary>
    /// <remarks>
    /// The <em>resolved</em> instance is asserted, not only that one descriptor survived. A count of one is
    /// logically sufficient under <c>TryAdd</c>, but it proves the claim in the test's name only by way of an
    /// argument about DI semantics — and it would keep passing if a future refactor registered the driver's
    /// probe first and the host's second.
    /// </remarks>
    [Fact]
    public void A_host_supplied_probe_wins_over_the_drivers_default()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAlvoDataReachability>(new AlwaysReachable());
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));

        using var container = collection.BuildServiceProvider();

        collection.Count(service => service.ServiceType == typeof(IAlvoDataReachability)).ShouldBe(1);
        container.GetRequiredService<IAlvoDataReachability>().ShouldBeOfType<AlwaysReachable>();
    }

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

    /// <summary>The container a host gets from <c>UseSqlite</c> alone, owned and disposed by this fixture.</summary>
    /// <param name="connectionString">The store this container's probe asks.</param>
    private ServiceProvider Container(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite(connectionString));

        var container = collection.BuildServiceProvider();
        _containers.Add(container);

        return container;
    }
}
