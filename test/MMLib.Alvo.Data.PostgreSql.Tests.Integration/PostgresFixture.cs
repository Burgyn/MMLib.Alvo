using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Starts a single, real PostgreSQL container for the lifetime of the test class that shares this
/// fixture, so the contract suite exercises a real engine rather than a fake.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Explicit tag: PostgreSqlBuilder's parameterless ctor and its PostgreSqlImage constant are
    // both obsolete in Testcontainers.PostgreSql 4.13 in favor of an explicit image argument.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private bool _started;

    // Empty when the container was never started — see InitializeAsync. Every test/constructor
    // that consumes this must have already self-skipped via EnsureEngineAvailable() /
    // Assert.SkipUnless(!OperatingSystem.IsWindows()) before relying on it being non-empty.
    public string ConnectionString => _started ? _container.GetConnectionString() : string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Windows GitHub runners run Docker in Windows-container mode, which has no
        // linux/amd64 manifest for postgres:16-alpine ("no matching manifest" / "No such
        // image"). Every test using this fixture self-skips on Windows (see
        // EnsureEngineAvailable / Assert.SkipUnless below), so there is nothing to start here —
        // starting would just throw before any test got a chance to skip.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await _container.StartAsync();
        _started = true;
    }

    public ValueTask DisposeAsync() => _started ? _container.DisposeAsync() : ValueTask.CompletedTask;
}
