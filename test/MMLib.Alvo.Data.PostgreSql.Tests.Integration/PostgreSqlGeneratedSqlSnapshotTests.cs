using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the shared <see cref="SchemaSqlSnapshotTests"/> suite against a real PostgreSQL server,
/// wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point — the same path a host
/// application would use. See the equivalent <c>SqliteGeneratedSqlSnapshotTests</c> in
/// <c>MMLib.Alvo.Data.Sqlite.Tests</c> for the same canonical set on SQLite.
/// </summary>
/// <remarks>
/// <c>EfCoreSchemaMigrator.PlanAsync</c> never touches the underlying <c>DbConnection</c>
/// (only <c>ApplyAsync</c> does), so no database round trip happens here; the real container
/// (via <see cref="PostgresFixture"/>) is still used — the same public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point a host application
/// would use — to keep this wired exactly like the contract-suite integration tests, rather than
/// relying on PlanAsync's connection-less behavior as an implementation detail.
/// </remarks>
public sealed class PostgreSqlGeneratedSqlSnapshotTests : SchemaSqlSnapshotTests, IClassFixture<PostgresFixture>, IDisposable
{
    private readonly ServiceProvider _services;

    public PostgreSqlGeneratedSqlSnapshotTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (Windows-container runners can't run the
            // Linux postgres:16-alpine image), so fixture.ConnectionString is empty here — every
            // test below calls EnsureEngineAvailable() as its first statement and skips before
            // touching _services.
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(fixture.ConnectionString);
        _services = builder.Services.BuildServiceProvider();
    }

    protected override string EngineName => "postgres";

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    protected override ISchemaMigrator CreateMigrator() => _services.GetRequiredService<ISchemaMigrator>();

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
