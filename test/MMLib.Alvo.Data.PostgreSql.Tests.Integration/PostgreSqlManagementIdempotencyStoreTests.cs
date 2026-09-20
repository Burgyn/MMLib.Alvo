using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using MMLib.Alvo.Testing.Management;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the full <see cref="ManagementIdempotencyStoreContractTests"/> suite against a real PostgreSQL
/// server, wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point — the same path a host
/// application would use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine-agnostic core is a §0 principle, and one implementor is not a contract suite.</b>
/// <c>EfCoreManagementIdempotencyStore</c> is registered for <em>both</em> engines by
/// <c>AlvoEfCoreProvider</c>, and until this class existed only SQLite ran the facts — while both sibling
/// contract suites (<see cref="Migrations.DescriptorVersionStoreContractTests"/> and the outbox's) already
/// had two. The rules this suite pins are the ones an engine can disagree about quietly: what a reused key
/// with a different fingerprint does, and whether a scope really partitions the records.
/// </para>
/// <para>
/// The shape is <see cref="PostgreSqlDescriptorVersionStoreTests"/>' exactly — a container shared per class
/// through <see cref="PostgresFixture"/>, a fresh database per test instance, and the Windows self-skip
/// before anything touches the container.
/// </para>
/// </remarks>
public sealed class PostgreSqlManagementIdempotencyStoreTests
    : ManagementIdempotencyStoreContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";

    private readonly ServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlManagementIdempotencyStoreTests"/> class.</summary>
    /// <param name="fixture">The shared container.</param>
    public PostgreSqlManagementIdempotencyStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container, so every fact self-skips in EnsureEngineAvailable()
            // before CreateStore() reaches this container.
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        CreateDatabase(fixture.ConnectionString, _databaseName);

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(WithDatabase(fixture.ConnectionString, _databaseName));
        _services = builder.Services.BuildServiceProvider();
    }

    /// <inheritdoc/>
    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    /// <inheritdoc/>
    protected override IManagementIdempotencyStore CreateStore() =>
        _services.GetRequiredService<IManagementIdempotencyStore>();

    /// <summary>Disposes the container. The database goes with the fixture's own teardown.</summary>
    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates this instance's own database on the shared server.</summary>
    /// <param name="adminConnectionString">The fixture's connection string.</param>
    /// <param name="databaseName">The database to create.</param>
    private static void CreateDatabase(string adminConnectionString, string databaseName)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    /// <summary>The fixture's connection string, pointed at <paramref name="databaseName"/>.</summary>
    /// <param name="connectionString">The fixture's connection string.</param>
    /// <param name="databaseName">The database this instance owns.</param>
    private static string WithDatabase(string connectionString, string databaseName) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;

    /// <summary>The minimal <see cref="IAlvoBuilder"/> the provider extension needs.</summary>
    /// <param name="services">The collection the provider registers into.</param>
    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
}
