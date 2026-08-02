using MMLib.Alvo.Tests.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Starts a single, real PostgreSQL container for the lifetime of the test class that shares this
/// fixture, so the contract suite exercises a real engine rather than a fake.
/// </summary>
/// <remarks>
/// The container-creation mechanics live in <see cref="PostgresTestContainer"/>, shared with the API suite's
/// <c>PostgresApiEngine</c> — a second copy of the image tag and the lazy-build discipline here is exactly
/// how the two would quietly drift apart. This fixture keeps its own policy: a Windows-container runner has
/// no linux/amd64 manifest for <c>postgres:16-alpine</c>, so it self-skips before ever calling
/// <see cref="PostgresTestContainer.BuildAndStartAsync"/>; Linux stays strict, because skipping there would
/// silently drop the entire real-PostgreSQL leg of the suite.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Built inside InitializeAsync, never in a field initializer — see PostgresTestContainer.
    private PostgreSqlContainer? _container;

    // Empty when the container was never started — see InitializeAsync. Every test/constructor
    // that consumes this must have already self-skipped via EnsureEngineAvailable() /
    // Assert.SkipUnless(!OperatingSystem.IsWindows()) before relying on it being non-empty.
    public string ConnectionString => _container is not null ? _container.GetConnectionString() : string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _container = await PostgresTestContainer.BuildAndStartAsync();
    }

    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;
}
