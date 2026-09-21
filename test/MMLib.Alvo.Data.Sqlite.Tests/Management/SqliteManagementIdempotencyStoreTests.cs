using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using MMLib.Alvo.Testing.Management;

namespace MMLib.Alvo.Data.Sqlite.Tests.Management;

/// <summary>
/// Runs the full <see cref="ManagementIdempotencyStoreContractTests"/> suite against a real SQLite database
/// file, wired exclusively through the public <see cref="AlvoSqliteBuilderExtensions.UseSqlite"/> entry
/// point — the same fixture shape <see cref="SqliteDescriptorVersionStoreTests"/> uses, and the same reason:
/// resolving the port from the container is also the fact that the provider registers it at all.
/// </summary>
public sealed class SqliteManagementIdempotencyStoreTests : ManagementIdempotencyStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-management-idempotency-tests-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="SqliteManagementIdempotencyStoreTests"/> class.</summary>
    public SqliteManagementIdempotencyStoreTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    /// <inheritdoc/>
    protected override IManagementIdempotencyStore CreateStore() =>
        _services.GetRequiredService<IManagementIdempotencyStore>();

    /// <summary>Disposes the container and best-effort deletes the temporary database file.</summary>
    public void Dispose()
    {
        _services.Dispose();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The minimal <see cref="IAlvoBuilder"/> the provider extension needs.</summary>
    /// <param name="services">The collection the provider registers into.</param>
    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
