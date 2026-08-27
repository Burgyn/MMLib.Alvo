using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class EfCoreSchemaMigratorApplyTests : IDisposable
{
    // A named, shared-cache SQLite in-memory database: distinct connections that share the same
    // "Data Source" name + Cache=Shared attach to the SAME in-memory database, which is what lets
    // the migrator's and introspector's independent per-call connections (RelationalConnectionFactory)
    // see each other's writes. A shared-cache in-memory database is destroyed once its last
    // connection closes, so _keepAlive holds one dedicated, never-handed-out connection open for
    // the fixture's lifetime — the per-call connections created by _connections come and go.
    private readonly string _connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly RelationalConnectionFactory _connections;
    private readonly DbContext _ctx;
    private readonly EfCoreSchemaMigrator _migrator;
    private readonly EfCoreSchemaIntrospector _introspector;

    public EfCoreSchemaMigratorApplyTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _connections = new RelationalConnectionFactory(() => new SqliteConnection(_connectionString));

        _ctx = new DbContext(new DbContextOptionsBuilder().UseSqlite(_keepAlive).Options);
        _migrator = CreateMigrator(_connections, new TestSqlDialect());
        // IDatabaseModelFactory is a design-time-only service (never registered by the runtime
        // UseSqlite pipeline), so it's resolved through the same reflective bootstrap `dotnet-ef`
        // itself uses: DesignTimeServicesBuilder reads the [DesignTimeProviderServices] attribute
        // off the Sqlite assembly and instantiates its (internal) SqliteDesignTimeServices.
        var designTimeServices = new DesignTimeServicesBuilder(
                GetType().Assembly, GetType().Assembly, new OperationReporter(handler: null), [])
            .Build(_ctx);
        _introspector = new EfCoreSchemaIntrospector(designTimeServices.GetRequiredService<IDatabaseModelFactory>(), _connections);
    }

    /// <summary>
    /// Builds a migrator over this fixture's EF services, with the connection factory and dialect the
    /// caller wants. Extracted because a fact about the framing needs both of those to differ, and
    /// re-resolving the design-time services per test would cost more than it explains.
    /// </summary>
    private EfCoreSchemaMigrator CreateMigrator(RelationalConnectionFactory connections, IAlvoSqlDialect dialect) =>
        new(
            _ctx.GetService<IMigrationsModelDiffer>(),
            _ctx.GetService<IMigrationsSqlGenerator>(),
            _ctx.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            connections,
            dialect,
            computed: null);

    public void Dispose()
    {
        _ctx.Dispose();
        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SchemaModel Empty => new([]);

    private static SchemaModel Vehicles => new([
        new EntitySchema
        {
            Name = "vehicles",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true },
                new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true },
            ],
        },
    ]);

    [Fact]
    public async Task Apply_creates_the_table_and_introspection_sees_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = await _migrator.PlanAsync(Empty, Vehicles, new MigrationOptions(), ct);

        var result = await _migrator.ApplyAsync(plan, new MigrationOptions(), ct);

        result.Applied.ShouldBeTrue();
        result.WasDryRun.ShouldBeFalse();

        var schema = await _introspector.IntrospectAsync(ct);
        var vehicles = schema.Entities.ShouldHaveSingleItem();
        vehicles.Name.ShouldBe("vehicles");

        var vin = vehicles.Fields.Single(f => f.Name == "vin");
        vin.Nullable.ShouldBeFalse();

        var note = vehicles.Fields.Single(f => f.Name == "note");
        note.Nullable.ShouldBeTrue();
    }

    [Fact]
    public async Task Dry_run_executes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = await _migrator.PlanAsync(Empty, Vehicles, new MigrationOptions(), ct);

        var result = await _migrator.ApplyAsync(plan, new MigrationOptions { DryRun = true }, ct);

        result.Applied.ShouldBeFalse();
        result.WasDryRun.ShouldBeTrue();

        var schema = await _introspector.IntrospectAsync(ct);
        schema.Entities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Destructive_change_is_refused_without_AllowDestructive()
    {
        var ct = TestContext.Current.CancellationToken;
        var createPlan = await _migrator.PlanAsync(Empty, Vehicles, new MigrationOptions(), ct);
        await _migrator.ApplyAsync(createPlan, new MigrationOptions(), ct);

        var dropPlan = await _migrator.PlanAsync(Vehicles, Empty, new MigrationOptions(), ct);
        dropPlan.HasDestructiveChanges.ShouldBeTrue();

        var result = await _migrator.ApplyAsync(dropPlan, new MigrationOptions { AllowDestructive = false }, ct);

        result.Applied.ShouldBeFalse();
        result.WasDryRun.ShouldBeFalse();

        var schema = await _introspector.IntrospectAsync(ct);
        schema.Entities.ShouldHaveSingleItem().Name.ShouldBe("vehicles");
    }

    [Fact]
    public async Task Reapplying_the_same_schema_produces_an_empty_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        var createPlan = await _migrator.PlanAsync(Empty, Vehicles, new MigrationOptions(), ct);
        await _migrator.ApplyAsync(createPlan, new MigrationOptions(), ct);

        var noopPlan = await _migrator.PlanAsync(Vehicles, Vehicles, new MigrationOptions(), ct);

        noopPlan.IsEmpty.ShouldBeTrue();
    }

    // --- add+drop on one table: EF's guessed-rename guardrail bypass (Finding A) ---

    private const string RowId = "11111111-1111-1111-1111-111111111111";

    private static SchemaModel OneField(string fieldName, FieldType type = FieldType.String) => new([
        new EntitySchema
        {
            Name = "vehicles",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = fieldName, Type = type, Nullable = true },
            ],
        },
    ]);

    [Fact]
    public async Task Drop_and_add_same_type_is_destructive_and_does_not_carry_data_over()
    {
        // Drop undeclared "a" + add same-type "d" in one apply. EF's differ guesses a single
        // RenameColumn a->d; if accepted, "a"'s data slips into the unrelated "d" AND a destructive
        // change applies without AllowDestructive. Our splitter must turn it into Drop + Add.
        var ct = TestContext.Current.CancellationToken;
        var before = OneField("a");
        var after = OneField("d");

        await _migrator.ApplyAsync(await _migrator.PlanAsync(Empty, before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, a) VALUES ('{RowId}', 'hello')", ct);

        var plan = await _migrator.PlanAsync(before, after, new MigrationOptions(), ct);

        // (a) The guessed rename is reclassified: a genuine destructive drop + a non-destructive add.
        plan.HasDestructiveChanges.ShouldBeTrue();
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.DropField && s.Change.Field == "a");
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.AddField && s.Change.Field == "d");
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.RenameField);

        // Refused without AllowDestructive.
        var refused = await _migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = false }, ct);
        refused.Applied.ShouldBeFalse();

        // (b) Applied WITH AllowDestructive: "a" is gone, "d" exists, and "d" did NOT inherit "a"'s data.
        var applied = await _migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = true }, ct);
        applied.Applied.ShouldBeTrue();

        var vehicles = (await _introspector.IntrospectAsync(ct)).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "d");
        vehicles.Fields.ShouldNotContain(f => f.Name == "a");

        (await QueryScalarAsync($"SELECT d FROM vehicles WHERE id = '{RowId}'", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Drop_and_add_different_type_applies_without_no_such_column_error()
    {
        // Drop "a" (string) + add "n" (integer): different types, so EF emits DropColumn + AddColumn
        // (two ops). Per-operation SQL generation made the drop's SQLite table-rebuild SELECT the
        // not-yet-added "n" ("no such column: n"); whole-plan generation excludes it. (Finding B.)
        var ct = TestContext.Current.CancellationToken;
        var before = OneField("a");
        var after = OneField("n", FieldType.Integer);

        await _migrator.ApplyAsync(await _migrator.PlanAsync(Empty, before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, a) VALUES ('{RowId}', 'hello')", ct);

        var plan = await _migrator.PlanAsync(before, after, new MigrationOptions(), ct);

        var result = await _migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = true }, ct);
        result.Applied.ShouldBeTrue();

        var vehicles = (await _introspector.IntrospectAsync(ct)).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "n");
        vehicles.Fields.ShouldNotContain(f => f.Name == "a");
    }

    [Fact]
    public async Task Declared_rename_still_preserves_data()
    {
        // Regression guard: a DECLARED rename (RenamedFrom) must remain a genuine, non-destructive
        // rename that preserves data — the Finding-A fix must not turn real renames into drop+add.
        var ct = TestContext.Current.CancellationToken;
        var before = OneField("colour");
        var after = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true, RenamedFrom = "colour" },
                ],
            },
        ]);

        await _migrator.ApplyAsync(await _migrator.PlanAsync(Empty, before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, colour) VALUES ('{RowId}', 'red')", ct);

        var plan = await _migrator.PlanAsync(before, after, new MigrationOptions(), ct);

        plan.HasDestructiveChanges.ShouldBeFalse();
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.RenameField && s.Change.Field == "color");
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.DropField);

        var result = await _migrator.ApplyAsync(plan, new MigrationOptions(), ct);
        result.Applied.ShouldBeTrue();

        var vehicles = (await _introspector.IntrospectAsync(ct)).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "color");
        (await QueryScalarAsync($"SELECT color FROM vehicles WHERE id = '{RowId}'", ct)).ShouldBe("red");
    }

    /// <summary>The table the framing writes its marker into, so a test can see whether the restore ran.</summary>
    private const string FramingLog = "alvo_framing_log";

    /// <summary>
    /// A migration cancelled once it is under way still restores the framing, and the cancellation is what
    /// the caller sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What goes wrong without it is not a failed migration but a poisoned connection.</b> The restore
    /// used to run on the caller's token, which is already cancelled on precisely the path the restore
    /// exists for — so <c>PRAGMA foreign_keys = 1</c> never reached the connection, and the connection went
    /// back to the pool with enforcement suspended. The next borrower writes children against no foreign key
    /// at all, in a request that has nothing to do with the migration and reports nothing wrong.
    /// </para>
    /// <para>
    /// <b>The cancellation is armed by the connection factory, and it has to be.</b>
    /// <see cref="EfCoreSchemaMigrator.ApplyAsync"/> guards on the token before it creates a connection, so a
    /// token cancelled any earlier is refused there and the framed execution is never entered — the fact
    /// would pass without measuring anything. Creating the connection is the last step before the framing
    /// runs, which makes it the one seam that puts the cancellation exactly where the bug lives.
    /// </para>
    /// <para>
    /// <b>The framing's <c>Before</c> half is deliberately empty.</b> Every statement in it would run on the
    /// already-cancelled token and throw <em>outside</em> the try, where no restore is owed and none should
    /// happen — a real dialect's suspension simply cannot be reached in this state. Leaving it empty isolates
    /// the half under test: the restore, which must run regardless.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_migration_still_restores_the_framing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecAsync($"CREATE TABLE {FramingLog} (marker TEXT NOT NULL)", ct);
        var plan = await _migrator.PlanAsync(Empty, Vehicles, new MigrationOptions(), ct);

        var cancelled = new CancellationTokenSource();
        var connections = new RelationalConnectionFactory(() =>
        {
            cancelled.Cancel();
            return new SqliteConnection(_connectionString);
        });
        var migrator = CreateMigrator(connections, new RestoreLoggingDialect());

        await Should.ThrowAsync<OperationCanceledException>(
            () => migrator.ApplyAsync(plan, new MigrationOptions(), cancelled.Token));

        (await QueryScalarAsync($"SELECT marker FROM {FramingLog}", ct))
            .ShouldBe("restored", "the restore must not take the token that cancelled the migration");
    }

    /// <summary>
    /// <see cref="TestSqlDialect"/> with a restore that leaves a row behind, which is the only way a test can
    /// see whether it ran: the real framing's <c>PRAGMA foreign_keys</c> is connection state, and the
    /// migrator disposes its connection before returning.
    /// </summary>
    private sealed class RestoreLoggingDialect : IAlvoSqlDialect
    {
        private readonly TestSqlDialect _inner = new();

        public MigrationBatchFraming MigrationFraming { get; } = new()
        {
            After = [$"INSERT INTO {FramingLog} (marker) VALUES ('restored')"],
        };

        public string RowLockClause(PreImageMutation mutation) => _inner.RowLockClause(mutation);

        public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) =>
            _inner.RenderTable(entity, lockedPreImageFor);

        public string RenderColumn(string columnName) => _inner.RenderColumn(columnName);

        public string RenderNullProjection(string storeType) => _inner.RenderNullProjection(storeType);

        public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) =>
            _inner.DecodeConstraintViolation(failure);
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        var command = _keepAlive.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<object?> QueryScalarAsync(string sql, CancellationToken ct)
    {
        var command = _keepAlive.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(ct);
            return value is DBNull ? null : value;
        }
    }
}
