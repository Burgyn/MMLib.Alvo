using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using VerifyXunit;
using Xunit;
using static VerifyXunit.Verifier;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// Freezes the DDL that <c>EfCoreSchemaMigrator.PlanAsync</c> generates for a canonical change set
/// on a "vehicles" entity (mirroring <c>examples/vehicle-registry</c>) — an EF-version drift guard:
/// a provider bump that silently changes the generated SQL breaks one of these snapshots instead
/// of going unnoticed. Inherit this from a concrete test class that wires
/// <see cref="CreateMigrator"/> and <see cref="EngineName"/> to the provider under test; each
/// provider verifies against its own <c>.verified.txt</c> files, kept next to the derived test
/// project by the repo's shared Verify <c>DerivePathInfo</c> module initializer
/// (<c>test/_shared/VerifyModuleInit.cs</c>).
/// </summary>
public abstract class SchemaSqlSnapshotTests
{
    /// <summary>Creates the <see cref="ISchemaMigrator"/> under test.</summary>
    /// <returns>The migrator instance to exercise.</returns>
    protected abstract ISchemaMigrator CreateMigrator();

    /// <summary>The engine name (e.g. "sqlite", "postgres") used to keep each provider's verified files distinct.</summary>
    protected abstract string EngineName { get; }

    /// <summary>
    /// Hook called as the first statement of every real test below. No-op for engines that are
    /// always available (SQLite); a real-engine provider overrides this to dynamically skip when
    /// its engine cannot run in the current environment (e.g. PostgreSQL Testcontainers on a
    /// Windows-container CI runner).
    /// </summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>Creating the "vehicles" table from an empty schema.</summary>
    [Fact]
    public async Task Create_vehicles_table_sql_is_stable()
    {
        EnsureEngineAvailable();
        var plan = await CreateMigrator().PlanAsync(Empty(), Model(Vehicles()), new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>Adding a plain column to an existing "vehicles" table.</summary>
    [Fact]
    public async Task Add_column_sql_is_stable()
    {
        EnsureEngineAvailable();
        var before = Model(Vehicles());
        var after = Model(Vehicles([new FieldSchema { Name = "mileage", Type = FieldType.Integer }]));

        var plan = await CreateMigrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>Renaming a column via <see cref="FieldSchema.RenamedFrom"/> (must preserve data, not drop+add).</summary>
    [Fact]
    public async Task Rename_column_sql_is_stable()
    {
        EnsureEngineAvailable();
        var before = Model(Vehicles([new FieldSchema { Name = "colour", Type = FieldType.String, MaxLength = 30 }]));
        var after = Model(Vehicles(
        [
            new FieldSchema { Name = "color", Type = FieldType.String, MaxLength = 30, RenamedFrom = "colour" },
        ]));

        var plan = await CreateMigrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>Dropping a column from "vehicles".</summary>
    [Fact]
    public async Task Drop_column_sql_is_stable()
    {
        EnsureEngineAvailable();
        var before = Model(Vehicles([new FieldSchema { Name = "mileage", Type = FieldType.Integer }]));
        var after = Model(Vehicles());

        var plan = await CreateMigrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>Adding a composite (non-unique) index on "vehicles" (make, model).</summary>
    [Fact]
    public async Task Add_composite_index_sql_is_stable()
    {
        EnsureEngineAvailable();
        var before = Model(Vehicles());
        var after = Model(Vehicles(indexes: [new IndexSchema(["make", "model"], Unique: false)]));

        var plan = await CreateMigrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>Adding a ref/FK field ("vehicles.owner_id" → "owners").</summary>
    [Fact]
    public async Task Add_ref_foreign_key_sql_is_stable()
    {
        EnsureEngineAvailable();
        var before = Model(Owners(), Vehicles());
        var after = Model(Owners(), Vehicles(
        [
            new FieldSchema { Name = "owner_id", Type = FieldType.Ref, Required = true, Reference = new RefSchema("owners", OnDelete.Restrict) },
        ]));

        var plan = await CreateMigrator().PlanAsync(before, after, new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    private Task VerifySql(MigrationPlan plan) => Verify(Sql(plan)).UseParameters(EngineName);

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
}
