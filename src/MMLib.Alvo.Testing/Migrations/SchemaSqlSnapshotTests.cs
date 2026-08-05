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

    /// <summary>
    /// Creating an entity covering every scalar field type, with precision/scale, and both a
    /// required and a nullable column of the same type — freezes the per-engine DDL for the whole
    /// type map (numeric/date/timestamp/boolean/uuid/json/enum) and for NULL vs NOT NULL, which the
    /// other cases (all uuid/string/integer, all NOT NULL) never exercise.
    /// </summary>
    [Fact]
    public async Task Create_entity_with_every_field_type_sql_is_stable()
    {
        EnsureEngineAvailable();
        var plan = await CreateMigrator().PlanAsync(Empty(), Model(Catalog()), new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>
    /// Creating a fully-managed entity — tenant_id (indexed) plus the audit and soft-delete columns
    /// exactly as <c>DescriptorToSchemaMapper</c> injects them — freezes the DDL of the columns that
    /// appear on every real Alvo table: the tenant index, the NOT NULL audit timestamps, and the
    /// nullable actor/deleted_at columns. (The mapper's production of these columns is guarded
    /// separately by DescriptorToSchemaMapperTests; this freezes the SQL they turn into.)
    /// </summary>
    [Fact]
    public async Task Create_audited_tenant_entity_sql_is_stable()
    {
        EnsureEngineAvailable();
        var plan = await CreateMigrator().PlanAsync(Empty(), Model(AuditedTenant()), new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    /// <summary>
    /// Creating an entity whose <c>total</c> field is <c>computed</c> — freezes the <b>generation clause</b> each
    /// engine spells for a stored generated column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the EF-version drift guard the whole mechanism rests on.</b> Alvo never spells this DDL: the
    /// model marks the property <c>HasComputedColumnSql(…, stored: true)</c> and EF's own per-provider generator
    /// emits it — <c>numeric(18,2) GENERATED ALWAYS AS (…) STORED</c> on PostgreSQL, the legal short form
    /// <c>AS (…) STORED</c> on SQLite. A provider bump that stopped emitting the clause would ship an
    /// <em>ordinary column nothing maintains</em>, with every behavioural fact still green because a column that
    /// merely holds the right number reads identically. Here it breaks a snapshot instead.
    /// </para>
    /// <para>
    /// The expression is field-only arithmetic, which is not a simplification: a <c>computed</c> carrying a
    /// literal is refused at apply, because the scalar renderer routes every literal through a bind parameter and
    /// DDL has no bind-parameter form.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Create_entity_with_a_computed_field_sql_is_stable()
    {
        EnsureEngineAvailable();
        var plan = await CreateMigrator().PlanAsync(Empty(), Model(Lines()), new MigrationOptions(), TestContext.Current.CancellationToken);
        await VerifySql(plan);
    }

    private Task VerifySql(MigrationPlan plan) => Verify(Sql(plan)).UseParameters(EngineName);

    private static string Sql(MigrationPlan plan) => string.Join("\n;\n", plan.Sql);

    private static SchemaModel Empty() => new([]);

    private static SchemaModel Model(params EntitySchema[] entities) => new(entities);

    private static EntitySchema Catalog() => new()
    {
        Name = "catalog",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "name", Type = FieldType.String, MaxLength = 100, Required = true },
            new FieldSchema { Name = "description", Type = FieldType.Text, Required = true },
            new FieldSchema { Name = "quantity", Type = FieldType.Integer, Required = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Precision = 18, Scale = 2, Required = true },
            new FieldSchema { Name = "is_active", Type = FieldType.Boolean, Required = true },
            new FieldSchema { Name = "released_on", Type = FieldType.Date, Required = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "metadata", Type = FieldType.Json, Required = true },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["draft", "published"], Required = true },
            new FieldSchema { Name = "notes", Type = FieldType.String, MaxLength = 200, Nullable = true },
        ],
    };

    // The managed columns mirror DescriptorToSchemaMapper.AddManagedColumns for an entity with
    // tenancy=scoped, audit=true, softDelete=true.
    private static EntitySchema AuditedTenant() => new()
    {
        Name = "documents",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, MaxLength = 200, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "created_by", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "updated_by", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "deleted_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };

    /// <summary>
    /// The computed-column case's entity: two ordinary columns and one generated from them, mirroring
    /// <c>baas-analyza:1358</c>'s <c>invoice_items.line_total = unit_price * amount</c>.
    /// </summary>
    private static EntitySchema Lines() => new()
    {
        Name = "lines",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "unit_price", Type = FieldType.Decimal, Precision = 18, Scale = 2, Required = true },
            new FieldSchema { Name = "amount", Type = FieldType.Integer, Required = true },
            new FieldSchema
            {
                Name = "total",
                Type = FieldType.Decimal,
                Precision = 18,
                Scale = 2,
                Nullable = true,
                ComputedExpression = "unit_price * amount",
            },
        ],
    };

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
