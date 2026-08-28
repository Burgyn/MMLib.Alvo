using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Binds the two hand-maintained destructive-classification encodings so they cannot silently
/// diverge: the REAL path (<see cref="DestructiveScan"/>, which <see cref="EfCoreSchemaMigrator.PlanAsync"/>
/// consults to set each step's <c>IsDestructive</c>) and the FAKE path (<see cref="SchemaDiff"/>,
/// which drives <c>InMemorySchemaMigrator</c> and, through it, <c>RuntimeSchemaService</c> unit tests
/// and <c>RollbackPropertyTests</c>). Both are fed the exact same <c>(current, desired)</c>
/// <see cref="SchemaModel"/> pair per scenario and must agree on whether the change is destructive.
/// </summary>
/// <remarks>
/// <para>
/// <b>SQLite affinity caveat.</b> SQLite's EF provider erases <c>MaxLength</c>/<c>Precision</c>/
/// <c>Scale</c> to plain TEXT/INTEGER/REAL column affinity (see the comment on
/// <c>EfCoreSchemaMigratorPlanTests.AlterColumn</c>), so a length/precision/scale-only
/// <em>narrowing</em> never reaches the real path as an <see cref="AlterColumnOperation"/> at all —
/// <see cref="EfCoreSchemaMigrator.PlanAsync"/> would report "not destructive" merely because no
/// operation was generated, not because the change is genuinely safe. Asserting parity for those
/// scenarios on SQLite would therefore be a false positive, so they are deliberately EXCLUDED here:
/// shrinking MaxLength, an unbounded field newly gaining a MaxLength, shrinking Precision, and
/// shrinking Scale. Those four are already covered directly against <see cref="DestructiveScan"/>
/// (bypassing SQLite's affinity erasure) by the hand-built-operation tests in
/// <c>EfCoreSchemaMigratorPlanTests</c>. <em>Widening</em> a bound is unaffected by this gap — SQLite
/// reports no column change either way, so both paths trivially agree "not destructive" — so it IS
/// included below.
/// </para>
/// <para>
/// Bound on SQLite (8 scenarios): widen MaxLength, nullable→required, required→nullable, type
/// change, add field, drop field, rename field, drop entity. Both paths agreed on all of them.
/// </para>
/// </remarks>
public class DestructiveClassificationParityTests
{
    private static MigrationOptions Options => new();

    // Resolves the SQLite provider services from a throwaway DbContext, same wiring as
    // EfCoreSchemaMigratorPlanTests.NewSqliteMigrator — PlanAsync never touches the connection, so a
    // factory that always hands back the same unopened connection is enough.
    private static EfCoreSchemaMigrator NewSqliteMigrator()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        var ctx = new DbContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);
        return new EfCoreSchemaMigrator(
            ctx.GetService<IMigrationsModelDiffer>(),
            ctx.GetService<IMigrationsSqlGenerator>(),
            ctx.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            new RelationalConnectionFactory(() => connection),
            new TestSqlDialect(),
            computed: null);
    }

    private static EntitySchema Widgets(params FieldSchema[] fields) => new() { Name = "widgets", Fields = fields };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    public static TheoryData<string, SchemaModel, SchemaModel, bool> Scenarios()
    {
        var data = new TheoryData<string, SchemaModel, SchemaModel, bool>();

        data.Add(
            "Widen MaxLength",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 20, Nullable = true })]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "code", Type = FieldType.String, MaxLength = 50, Nullable = true })]),
            false);

        data.Add(
            "Nullable to required",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true })]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Required = true, Nullable = false })]),
            true);

        data.Add(
            "Required to nullable",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Required = true, Nullable = false })]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "note", Type = FieldType.String, Nullable = true })]),
            false);

        data.Add(
            "Type change string to integer",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "value", Type = FieldType.String, Required = true, Nullable = false })]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "value", Type = FieldType.Integer, Required = true, Nullable = false })]),
            true);

        data.Add(
            "Add field",
            new SchemaModel([Widgets(Id())]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true })]),
            false);

        data.Add(
            "Drop field",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true })]),
            new SchemaModel([Widgets(Id())]),
            true);

        data.Add(
            "Rename field",
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "colour", Type = FieldType.String, Nullable = true })]),
            new SchemaModel([Widgets(Id(), new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true, RenamedFrom = "colour" })]),
            false);

        data.Add(
            "Drop entity",
            new SchemaModel([Widgets(Id())]),
            new SchemaModel([]),
            true);

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Real_and_fake_paths_agree_on_destructiveness(
        string scenario, SchemaModel current, SchemaModel desired, bool expectedDestructive)
    {
        var migrator = NewSqliteMigrator();
        var plan = await migrator.PlanAsync(current, desired, Options, TestContext.Current.CancellationToken);
        var realIsDestructive = plan.HasDestructiveChanges;

        var fakeSteps = SchemaDiff.Compute(current, desired);
        var fakeIsDestructive = fakeSteps.Any(s => s.IsDestructive);

        realIsDestructive.ShouldBe(expectedDestructive, $"{scenario}: real (DestructiveScan) path");
        fakeIsDestructive.ShouldBe(expectedDestructive, $"{scenario}: fake (SchemaDiff) path");
        realIsDestructive.ShouldBe(fakeIsDestructive, $"{scenario}: real vs fake parity");
    }
}
