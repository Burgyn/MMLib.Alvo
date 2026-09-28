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

    /// <summary>
    /// A plan that rebuilds a table other rows reference — SQLite's create-new / copy / drop / rename, which is what
    /// EF emits for a generated column added to a populated table — applies on the runtime path and keeps the child.
    /// </summary>
    /// <remarks>
    /// The writer must frame its transaction with the dialect's <c>PRAGMA foreign_keys = 0</c> the way the migrator
    /// does: the pragma is a no-op inside a transaction, so without the framing <c>DROP TABLE parents</c> is refused
    /// by the restricted reference (<c>FOREIGN KEY constraint failed</c>) — the failure the dashboard's apply of a
    /// computed field on <c>customers</c> hit — and a cascading one would have deleted the children instead.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_of_a_referenced_table_applies_and_keeps_its_children()
    {
        var writer = CreateWriter();
        var ct = TestContext.Current.CancellationToken;
        await writer.ApplyAndAppendAsync("rebuild", Plan(
            "CREATE TABLE parents (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE children (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parents (id) ON DELETE RESTRICT)",
            "INSERT INTO parents (id, name) VALUES (1, 'p')",
            "INSERT INTO children (id, parent_id) VALUES (10, 1)"), Candidate(0), 0, new MigrationOptions(), ct);

        await writer.ApplyAndAppendAsync("rebuild", Plan(
            "CREATE TABLE ef_temp_parents (id INTEGER PRIMARY KEY, name TEXT, label TEXT AS (name) STORED)",
            "INSERT INTO ef_temp_parents (id, name) SELECT id, name FROM parents",
            "DROP TABLE parents",
            "ALTER TABLE ef_temp_parents RENAME TO parents"), Candidate(1), 1, new MigrationOptions(), ct);

        (await ScalarAsync("SELECT count(*) FROM children WHERE parent_id = 1", ct)).ShouldBe(1L);
        (await ScalarAsync("SELECT label FROM parents WHERE id = 1", ct)).ShouldBe("p");
    }

    /// <summary>
    /// A rebuild that introduces a reference over a value naming no parent is refused before it commits — on the
    /// runtime path — naming the table, the column and the row, and nothing of it is applied.
    /// </summary>
    /// <remarks>
    /// With enforcement suspended for the rebuild, the orphan used to be copied into a table whose foreign key it
    /// violates and committed silently. <c>PRAGMA foreign_key_check</c> inside the transaction (SQLite's own step 10)
    /// is what turns that into a refusal.
    /// </remarks>
    [Fact]
    public async Task A_rebuild_that_would_commit_an_orphaned_reference_is_refused_and_rolled_back()
    {
        var writer = CreateWriter();
        var ct = TestContext.Current.CancellationToken;
        await writer.ApplyAndAppendAsync("orphans", Plan(
            "CREATE TABLE parents (id INTEGER PRIMARY KEY)",
            "CREATE TABLE children (id INTEGER PRIMARY KEY, parent_id INTEGER)",
            "INSERT INTO parents (id) VALUES (1)",
            "INSERT INTO children (id, parent_id) VALUES (10, 1), (11, 99)"), Candidate(0), 0, new MigrationOptions(), ct);

        var refusal = await Should.ThrowAsync<MMLib.Alvo.Descriptor.DescriptorValidationException>(
            () => writer.ApplyAndAppendAsync("orphans", Plan(_orphaningRebuild), Candidate(1), 1, new MigrationOptions(), ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/children/fields/parent_id");
        error.Message.ShouldContain("'parents'");
        error.Message.ShouldContain("row 11");
        (await _services.GetRequiredService<IDescriptorVersionStore>().ListAsync("orphans", ct)).Count
            .ShouldBe(1, "the version row rolled back with the DDL");
        (await ScalarAsync("SELECT count(*) FROM pragma_foreign_key_list('children')", ct))
            .ShouldBe(0L, "the rebuild rolled back: the table has no foreign key");
    }

    /// <summary>The migrator's own apply verifies the same way.</summary>
    [Fact]
    public async Task The_migrators_apply_refuses_the_same_orphaned_reference()
    {
        var migrator = _services.GetRequiredService<ISchemaMigrator>();
        var ct = TestContext.Current.CancellationToken;
        await migrator.ApplyAsync(Plan(
            "CREATE TABLE parents (id INTEGER PRIMARY KEY)",
            "CREATE TABLE children (id INTEGER PRIMARY KEY, parent_id INTEGER)",
            "INSERT INTO children (id, parent_id) VALUES (11, 99)"), new MigrationOptions(), ct);

        var refusal = await Should.ThrowAsync<MMLib.Alvo.Descriptor.DescriptorValidationException>(
            () => migrator.ApplyAsync(Plan(_orphaningRebuild), new MigrationOptions(), ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/children/fields/parent_id");
        (await ScalarAsync("SELECT count(*) FROM pragma_foreign_key_list('children')", ct)).ShouldBe(0L);
    }

    /// <summary>
    /// The migration connections are unpooled, which is what makes closing one whose restore failed final: no pool
    /// can hand it out again with foreign keys still off.
    /// </summary>
    [Fact]
    public void Migration_connections_are_never_pooled()
    {
#pragma warning disable EF1001 // Alvo's own internal type; the analyzer keys on the ".Internal" namespace alone.
        using var connection = _services.GetRequiredService<MMLib.Alvo.Data.EntityFrameworkCore.Internal.RelationalConnectionFactory>().Create();
#pragma warning restore EF1001

        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString).Pooling.ShouldBeFalse();
    }

    /// <summary>The create-new / copy / drop / rename rebuild of <c>children</c>, adding a reference to <c>parents</c>.</summary>
    private static readonly string[] _orphaningRebuild =
    [
        "CREATE TABLE ef_temp_children (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parents (id))",
        "INSERT INTO ef_temp_children (id, parent_id) SELECT id, parent_id FROM children",
        "DROP TABLE children",
        "ALTER TABLE ef_temp_children RENAME TO children",
    ];

    private static MigrationPlan Plan(params string[] sql) => new() { Steps = [], Sql = sql };

    private static DescriptorVersion Candidate(int revision) =>
        new(new SchemaModel([]), "{}", Revision: revision, CreatedAt: DateTimeOffset.UnixEpoch);

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath};Foreign Keys=True");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
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
