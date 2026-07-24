using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// Behavioral contract every <see cref="ISchemaMigrator"/> implementation must
/// satisfy — DB-less fake and real provider alike. Inherit this from a concrete
/// test class that wires <see cref="CreateMigrator"/> and
/// <see cref="IntrospectAsync"/> to the provider under test.
/// </summary>
public abstract class SchemaMigratorContractTests
{
    /// <summary>Creates the <see cref="ISchemaMigrator"/> under test.</summary>
    /// <returns>The migrator instance to exercise.</returns>
    protected abstract ISchemaMigrator CreateMigrator();

    /// <summary>Introspects the schema actually applied by the migrator under test.</summary>
    /// <returns>The introspected schema model.</returns>
    protected abstract Task<SchemaModel> IntrospectAsync();

    /// <summary>
    /// Hook called as the first statement of every real test below. No-op for engines that are
    /// always available (SQLite, in-memory); a real-engine provider overrides this to
    /// dynamically skip when its engine cannot run in the current environment (e.g. PostgreSQL
    /// Testcontainers on a Windows-container CI runner).
    /// </summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    private static SchemaModel Empty() => new([]);

    private static SchemaModel Vehicles(params FieldSchema[] extra) =>
        new([
            new EntitySchema
            {
                Name = "vehicles",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17 },
                    .. extra,
                ],
            },
        ]);

    /// <summary>Applying a plan built from an empty schema must produce the desired entity.</summary>
    [Fact]
    public async Task Create_then_introspect_matches_desired()
    {
        EnsureEngineAvailable();
        var migrator = CreateMigrator();
        var plan = await migrator.PlanAsync(Empty(), Vehicles(), new MigrationOptions());
        await migrator.ApplyAsync(plan, new MigrationOptions());

        var actual = await IntrospectAsync();

        actual.Entities.ShouldContain(e => e.Name == "vehicles");
    }

    /// <summary>Planning from a schema to itself must yield no steps.</summary>
    [Fact]
    public async Task Reapply_is_idempotent()
    {
        EnsureEngineAvailable();
        var migrator = CreateMigrator();
        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), Vehicles(), new MigrationOptions()), new MigrationOptions());

        var second = await migrator.PlanAsync(Vehicles(), Vehicles(), new MigrationOptions());

        second.IsEmpty.ShouldBeTrue();
    }

    /// <summary>A destructive plan must be refused when <see cref="MigrationOptions.AllowDestructive"/> is false.</summary>
    [Fact]
    public async Task Drop_without_AllowDestructive_is_refused()
    {
        EnsureEngineAvailable();
        var migrator = CreateMigrator();
        var withColour = Vehicles(new FieldSchema { Name = "colour", Type = FieldType.String });
        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), withColour, new MigrationOptions()), new MigrationOptions());

        var plan = await migrator.PlanAsync(withColour, Vehicles(), new MigrationOptions());

        plan.HasDestructiveChanges.ShouldBeTrue();
        var result = await migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = false });
        result.Applied.ShouldBeFalse();
    }

    /// <summary>A field marked with <see cref="FieldSchema.RenamedFrom"/> must produce a rename step, not a drop+add.</summary>
    [Fact]
    public async Task Rename_preserves_data()
    {
        EnsureEngineAvailable();
        var migrator = CreateMigrator();
        var before = Vehicles(new FieldSchema { Name = "colour", Type = FieldType.String });
        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), before, new MigrationOptions()), new MigrationOptions());

        var after = Vehicles(new FieldSchema { Name = "color", Type = FieldType.String, RenamedFrom = "colour" });
        var plan = await migrator.PlanAsync(before, after, new MigrationOptions());

        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.RenameField);
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.DropField);
    }

    /// <summary>
    /// Reserved parity leg: the same contract suite must pass over a dynamic
    /// (metadata-driven) entity once the dynamic driver lands.
    /// </summary>
    [Fact(Skip = "Dynamic driver lands in F7 — parity leg reserved (analysis §2.1).")]
    public Task Same_suite_passes_over_a_dynamic_entity() => Task.CompletedTask;
}
