using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using System.Data.Common;

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

    /// <summary>
    /// A failing DDL statement at an uncontested expected revision must (a) surface the ORIGINAL
    /// provider exception, never <see cref="DescriptorConcurrencyException"/> — nobody else moved the
    /// revision, so the post-failure re-read finds <c>actual == expectedRevision</c> and the writer's
    /// conflict-translation must fall through to rethrow rather than mint a spurious conflict — and
    /// (b) roll back the version-row insert together with the DDL: the insert-then-DDL-then-commit
    /// steps share one transaction, so a mid-transaction DDL failure must leave no orphaned version
    /// row behind, exactly as if the call had never happened.
    /// </summary>
    [Fact]
    public async Task DDL_failure_at_an_uncontested_revision_propagates_the_original_error_and_appends_nothing()
    {
        var writer = CreateWriter();
        var store = _services.GetRequiredService<IDescriptorVersionStore>();
        var ct = TestContext.Current.CancellationToken;

        // Valid DDL syntax, invalid semantically (the table does not exist) — SQLite raises this as
        // a genuine DbException, not a lock/constraint conflict, at an expectedRevision (0) nothing
        // else is contending for.
        var plan = new MigrationPlan { Steps = [], Sql = ["DROP TABLE nonexistent_xyz"] };
        var candidate = new DescriptorVersion(new SchemaModel([]), "{}", Revision: 0, CreatedAt: DateTimeOffset.UnixEpoch);

        var ex = await Should.ThrowAsync<DbException>(
            () => writer.ApplyAndAppendAsync("ddl-failure", plan, candidate, expectedRevision: 0, new MigrationOptions(), ct));

        // DescriptorConcurrencyException does not derive from DbException, so Should.ThrowAsync<DbException>
        // above already rules it out; this is belt-and-suspenders against a future base-type change.
        ex.ShouldNotBeOfType<DescriptorConcurrencyException>();

        (await store.ListAsync("ddl-failure", ct)).ShouldBeEmpty();
    }

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
