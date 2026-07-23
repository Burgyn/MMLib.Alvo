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
}
