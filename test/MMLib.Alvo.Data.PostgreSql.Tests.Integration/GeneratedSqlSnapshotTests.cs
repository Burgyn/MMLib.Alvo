using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Freezes the PostgreSQL DDL that <c>EfCoreSchemaMigrator.PlanAsync</c> generates for the SAME
/// canonical change set on a "vehicles" entity as
/// <c>MMLib.Alvo.Data.Sqlite.Tests.GeneratedSqlSnapshotTests</c> — an EF-version drift guard: a
/// Npgsql/EF Core provider bump that silently changes the generated SQL breaks one of these
/// snapshots instead of going unnoticed.
/// </summary>
/// <remarks>
/// <c>EfCoreSchemaMigrator.PlanAsync</c> never touches the underlying <c>DbConnection</c>
/// (only <c>ApplyAsync</c> does), so no database round trip happens here; the real container
/// (via <see cref="PostgresFixture"/>) is still used — the same public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point a host application
/// would use — to keep this wired exactly like the contract-suite integration tests, rather than
/// relying on PlanAsync's connection-less behavior as an implementation detail.
/// </remarks>
public sealed class GeneratedSqlSnapshotTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly ServiceProvider _services;

    public GeneratedSqlSnapshotTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(fixture.ConnectionString);
        _services = builder.Services.BuildServiceProvider();
    }

    /// <summary>Creating the "vehicles" table from an empty schema.</summary>
    [Fact]
    public async Task Create_vehicles_table_sql_is_stable()
    {
        var plan = await Migrator().PlanAsync(Empty(), Model(Vehicles()), new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    /// <summary>Adding a plain column to an existing "vehicles" table.</summary>
    [Fact]
    public async Task Add_column_sql_is_stable()
    {
        var before = Model(Vehicles());
        var after = Model(Vehicles([new FieldSchema { Name = "mileage", Type = FieldType.Integer }]));

        var plan = await Migrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    /// <summary>Renaming a column via <see cref="FieldSchema.RenamedFrom"/> (must preserve data, not drop+add).</summary>
    [Fact]
    public async Task Rename_column_sql_is_stable()
    {
        var before = Model(Vehicles([new FieldSchema { Name = "colour", Type = FieldType.String, MaxLength = 30 }]));
        var after = Model(Vehicles(
        [
            new FieldSchema { Name = "color", Type = FieldType.String, MaxLength = 30, RenamedFrom = "colour" },
        ]));

        var plan = await Migrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    /// <summary>Dropping a column from "vehicles".</summary>
    [Fact]
    public async Task Drop_column_sql_is_stable()
    {
        var before = Model(Vehicles([new FieldSchema { Name = "mileage", Type = FieldType.Integer }]));
        var after = Model(Vehicles());

        var plan = await Migrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    /// <summary>Adding a composite (non-unique) index on "vehicles" (make, model).</summary>
    [Fact]
    public async Task Add_composite_index_sql_is_stable()
    {
        var before = Model(Vehicles());
        var after = Model(Vehicles(indexes: [new IndexSchema(["make", "model"], Unique: false)]));

        var plan = await Migrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    /// <summary>Adding a ref/FK field ("vehicles.owner_id" → "owners").</summary>
    [Fact]
    public async Task Add_ref_foreign_key_sql_is_stable()
    {
        var before = Model(Owners(), Vehicles());
        var after = Model(Owners(), Vehicles(
        [
            new FieldSchema { Name = "owner_id", Type = FieldType.Ref, Required = true, Reference = new RefSchema("owners", OnDelete.Restrict) },
        ]));

        var plan = await Migrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(Sql(plan));
    }

    private ISchemaMigrator Migrator() => _services.GetRequiredService<ISchemaMigrator>();

    private static string Sql(MigrationPlan plan) => string.Join("\n;\n", plan.Sql);

    private static SchemaModel Empty() => new([]);

    private static SchemaModel Model(params EntitySchema[] entities) => new(entities);

    private static EntitySchema Owners() => new()
    {
        Name = "owners",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "name", Type = FieldType.String, MaxLength = 120, Required = true },
        ],
    };

    private static EntitySchema Vehicles(IReadOnlyList<FieldSchema>? extraFields = null, IReadOnlyList<IndexSchema>? indexes = null) => new()
    {
        Name = "vehicles",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true, Unique = true },
            new FieldSchema { Name = "make", Type = FieldType.String, MaxLength = 60, Required = true },
            new FieldSchema { Name = "model", Type = FieldType.String, MaxLength = 60, Required = true },
            new FieldSchema { Name = "year", Type = FieldType.Integer, Required = true },
            .. extraFields ?? [],
        ],
        Indexes = indexes ?? [],
    };

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
