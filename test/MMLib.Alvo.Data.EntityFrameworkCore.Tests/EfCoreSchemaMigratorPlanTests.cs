using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class EfCoreSchemaMigratorPlanTests
{
    private static MigrationOptions Options => new();

    // Resolves the SQLite provider services from a throwaway DbContext (the reusable helper lands
    // in Task 11); until then the test wires the migrator by hand. PlanAsync never touches the
    // connection, so an unopened one is enough to satisfy the (now required) ctor parameter.
    private static EfCoreSchemaMigrator NewSqliteMigrator()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        var ctx = new DbContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);
        return new EfCoreSchemaMigrator(
            ctx.GetService<IMigrationsModelDiffer>(),
            ctx.GetService<IMigrationsSqlGenerator>(),
            ctx.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            connection);
    }

    private static EntitySchema Vehicles(params FieldSchema[] fields) =>
        new() { Name = "vehicles", Fields = fields };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    [Fact]
    public async Task Create_from_empty_produces_a_create_entity_step_with_sql()
    {
        var migrator = NewSqliteMigrator();
        var desired = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true }),
        ]);

        var plan = await migrator.PlanAsync(new SchemaModel([]), desired, Options, TestContext.Current.CancellationToken);

        var step = plan.Steps.ShouldHaveSingleItem();
        step.Change.Kind.ShouldBe(SchemaChangeKind.CreateEntity);
        step.Change.Entity.ShouldBe("vehicles");
        step.Sql.ShouldNotBeNullOrWhiteSpace();
        step.Sql.ShouldContain("CREATE TABLE");
        step.IsDestructive.ShouldBeFalse();
    }

    [Fact]
    public async Task Renamed_field_produces_a_rename_step_and_no_drop()
    {
        var migrator = NewSqliteMigrator();
        var current = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "colour", Type = FieldType.String, Nullable = true }),
        ]);
        var desired = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true, RenamedFrom = "colour" }),
        ]);

        var plan = await migrator.PlanAsync(current, desired, Options, TestContext.Current.CancellationToken);

        var rename = plan.Steps.ShouldHaveSingleItem();
        rename.Change.Kind.ShouldBe(SchemaChangeKind.RenameField);
        rename.Change.Entity.ShouldBe("vehicles");
        rename.Change.Field.ShouldBe("color");
        rename.Change.FromName.ShouldBe("colour");
        rename.Sql.ShouldContain("RENAME", Case.Insensitive);
        rename.IsDestructive.ShouldBeFalse();

        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.DropField);
    }

    [Fact]
    public async Task Dropped_field_is_marked_destructive()
    {
        var migrator = NewSqliteMigrator();
        var current = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "colour", Type = FieldType.String, Nullable = true }),
        ]);
        var desired = new SchemaModel([Vehicles(Id())]);

        var plan = await migrator.PlanAsync(current, desired, Options, TestContext.Current.CancellationToken);

        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.DropField);
        var dropStep = plan.Steps.Single(s => s.Change.Kind == SchemaChangeKind.DropField);
        dropStep.Change.Field.ShouldBe("colour");
        dropStep.Change.IsDestructive.ShouldBeTrue();
        dropStep.IsDestructive.ShouldBeTrue();
        plan.HasDestructiveChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task Nullable_to_required_alter_is_destructive()
    {
        var migrator = NewSqliteMigrator();
        var current = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true }),
        ]);
        var desired = new SchemaModel([
            Vehicles(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Required = true, Nullable = false }),
        ]);

        var plan = await migrator.PlanAsync(current, desired, Options, TestContext.Current.CancellationToken);

        var alter = plan.Steps.Single(s => s.Change.Kind == SchemaChangeKind.AlterField);
        alter.Change.Field.ShouldBe("note");
        alter.IsDestructive.ShouldBeTrue();
        plan.HasDestructiveChanges.ShouldBeTrue();
    }

    // MaxLength/Precision do not survive into SQLite's relational model (strings and decimals both
    // map to TEXT regardless of length/precision), so a length-only change produces no AlterColumn
    // via PlanAsync on SQLite. The narrowing guard is therefore exercised directly against
    // DestructiveScan — a pure, provider-independent classifier — with hand-built operations.
    private static AlterColumnOperation AlterColumn(int? oldMaxLength, int? newMaxLength) => new()
    {
        Table = "vehicles",
        Name = "code",
        ClrType = typeof(string),
        IsNullable = false,
        MaxLength = newMaxLength,
        OldColumn = new AddColumnOperation
        {
            Table = "vehicles",
            Name = "code",
            ClrType = typeof(string),
            IsNullable = false,
            MaxLength = oldMaxLength,
        },
    };

    [Fact]
    public void Max_length_shrink_alter_is_destructive()
    {
        var change = DestructiveScan.Classify(AlterColumn(oldMaxLength: 100, newMaxLength: 20));

        change.Kind.ShouldBe(SchemaChangeKind.AlterField);
        change.Field.ShouldBe("code");
        change.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public void Unbounded_to_bounded_alter_is_destructive()
    {
        // Issue-1 case: an unbounded string gaining a MaxLength can truncate existing data, so a
        // newly-imposed bound must be flagged destructive (RED before the NarrowingReason fix).
        var change = DestructiveScan.Classify(AlterColumn(oldMaxLength: null, newMaxLength: 50));

        change.Kind.ShouldBe(SchemaChangeKind.AlterField);
        change.Field.ShouldBe("code");
        change.IsDestructive.ShouldBeTrue();
    }

    [Fact]
    public async Task Rename_field_in_composite_index_realigns_index_and_succeeds()
    {
        // Issue-2 case: the renamed field also participates in a composite index; the aligned
        // "current" model must rename the index's column too, or FinalizeModel() crashes.
        var migrator = NewSqliteMigrator();
        var current = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    Id(),
                    new FieldSchema { Name = "make", Type = FieldType.String, Required = true },
                    new FieldSchema { Name = "model_name", Type = FieldType.String, Required = true },
                ],
                Indexes = [new IndexSchema(["make", "model_name"], true)],
            },
        ]);
        var desired = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    Id(),
                    new FieldSchema { Name = "make", Type = FieldType.String, Required = true },
                    new FieldSchema { Name = "model", Type = FieldType.String, Required = true, RenamedFrom = "model_name" },
                ],
                Indexes = [new IndexSchema(["make", "model"], true)],
            },
        ]);

        var plan = await migrator.PlanAsync(current, desired, Options, TestContext.Current.CancellationToken);

        var rename = plan.Steps.Single(s => s.Change.Kind == SchemaChangeKind.RenameField);
        rename.Change.Field.ShouldBe("model");
        rename.Change.FromName.ShouldBe("model_name");
    }
}
