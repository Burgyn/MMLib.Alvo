using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the full <see cref="RuntimeSchemaWriterContractTests"/> suite against a real SQLite database
/// file, wired exclusively through the public <see cref="AlvoSqliteBuilderExtensions.UseSqlite"/>
/// entry point. The writer and the descriptor-version store resolve from one provider (one shared
/// <c>RelationalConnectionFactory</c> over one file), so the writer's appended rows are visible to
/// the store and to the writer's own next call — the fixture shape the optimistic-lock contract
/// needs.
/// </summary>
public sealed class SqliteRuntimeSchemaWriterTests : RuntimeSchemaWriterContractTests, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-runtime-writer-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteRuntimeSchemaWriterTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        _services = builder.Services.BuildServiceProvider();
    }

    protected override IRuntimeSchemaWriter CreateWriter() => _services.GetRequiredService<IRuntimeSchemaWriter>();

    public void Dispose()
    {
        _services.Dispose();

        // Best-effort: the writer/store dispose their (pooling-disabled) per-call connections above,
        // which is what actually releases the OS file handle. This is a temp file either way, so a
        // stray lock (e.g. an antivirus scan on Windows) should not fail the test — the OS reclaims
        // temp files regardless.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
