using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class EfCoreSchemaMigratorApplyTests : IDisposable
{
    // A single shared, already-open connection: ":memory:" SQLite DBs live only as long as their
    // one connection stays open, and it must be the exact instance handed to both the migrator
    // (executes SQL) and the introspector (reads the schema back) so they see the same database.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EfCoreSchemaMigrator _migrator;
    private readonly EfCoreSchemaIntrospector _introspector;

    public EfCoreSchemaMigratorApplyTests()
    {
        _connection.Open();

        var ctx = new DbContext(new DbContextOptionsBuilder().UseSqlite(_connection).Options);
        _migrator = new EfCoreSchemaMigrator(
            ctx.GetService<IMigrationsModelDiffer>(),
            ctx.GetService<IMigrationsSqlGenerator>(),
            ctx.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            _connection);
        // IDatabaseModelFactory is a design-time-only service (never registered by the runtime
        // UseSqlite pipeline), so it's resolved through the same reflective bootstrap `dotnet-ef`
        // itself uses: DesignTimeServicesBuilder reads the [DesignTimeProviderServices] attribute
        // off the Sqlite assembly and instantiates its (internal) SqliteDesignTimeServices.
        var designTimeServices = new DesignTimeServicesBuilder(
                GetType().Assembly, GetType().Assembly, new OperationReporter(handler: null), [])
            .Build(ctx);
        _introspector = new EfCoreSchemaIntrospector(designTimeServices.GetRequiredService<IDatabaseModelFactory>(), _connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
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

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<object?> QueryScalarAsync(string sql, CancellationToken ct)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(ct);
            return value is DBNull ? null : value;
        }
    }
}
