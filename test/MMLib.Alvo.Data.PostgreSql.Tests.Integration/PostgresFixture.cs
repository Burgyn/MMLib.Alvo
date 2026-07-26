using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Starts a single, real PostgreSQL container for the lifetime of the test class that shares this
/// fixture, so the contract suite exercises a real engine rather than a fake.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Built inside InitializeAsync, never in a field initializer: Build() itself talks to the Docker
    // daemon, so on a machine with no reachable daemon it throws while the fixture is being
    // *constructed* — which xUnit reports as every test in the sharing class failing, before any of
    // them reaches its own skip. That is exactly what a GitHub Windows runner is: it stopped serving
    // a daemon on npipe://./pipe/docker_engine, and all 28 tests here turned from skipped to
    // "Failed to connect to Docker endpoint" without a line of their code changing. Constructing
    // lazily keeps the skip below load-bearing regardless of what the host's daemon is doing.
    private PostgreSqlContainer? _container;

    // Empty when the container was never started — see InitializeAsync. Every test/constructor
    // that consumes this must have already self-skipped via EnsureEngineAvailable() /
    // Assert.SkipUnless(!OperatingSystem.IsWindows()) before relying on it being non-empty.
    public string ConnectionString => _container is not null ? _container.GetConnectionString() : string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Windows GitHub runners run Docker in Windows-container mode when they run it at all, and
        // that mode has no linux/amd64 manifest for postgres:16-alpine ("no matching manifest" /
        // "No such image"). Every test using this fixture self-skips on Windows (see
        // EnsureEngineAvailable / Assert.SkipUnless below), so there is nothing to start here —
        // starting would just throw before any test got a chance to skip. Linux stays strict on
        // purpose: skipping there would silently drop the entire real-PostgreSQL leg of the suite.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Explicit tag: PostgreSqlBuilder's parameterless ctor and its PostgreSqlImage constant are
        // both obsolete in Testcontainers.PostgreSql 4.13 in favor of an explicit image argument.
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        _container = container;
    }

    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;
}
