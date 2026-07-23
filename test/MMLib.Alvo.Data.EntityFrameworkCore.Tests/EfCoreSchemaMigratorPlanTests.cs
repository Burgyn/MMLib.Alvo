using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class EfCoreSchemaMigratorPlanTests
{
    private static MigrationOptions Options => new();

    // Resolves the SQLite provider services from a throwaway DbContext (the reusable helper lands
    // in Task 11); until then the test wires the migrator by hand.
    private static EfCoreSchemaMigrator NewSqliteMigrator()
    {
        var ctx = new DbContext(new DbContextOptionsBuilder().UseSqlite("Data Source=:memory:").Options);
        return new EfCoreSchemaMigrator(
            ctx.GetService<IMigrationsModelDiffer>(),
            ctx.GetService<IMigrationsSqlGenerator>(),
            ctx.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()));
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
}
