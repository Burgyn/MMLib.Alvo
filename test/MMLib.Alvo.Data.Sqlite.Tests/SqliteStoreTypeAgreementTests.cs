using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The "one store-type authority" invariant, stated as an assertion. Two EF models are built from the same
/// <see cref="SchemaModel"/>: the migration model, whose DDL creates the real columns, and the read model,
/// whose <c>IProperty.GetColumnType()</c> is what the driver's <c>RenderNullProjection</c> casts a masked
/// field's <c>NULL</c> to. They apply the same facets in two places, so if either drifts the projected
/// typed <c>NULL</c> gets a type the column does not have — a hard cast error on PostgreSQL, and a value
/// silently reshaped on a precision mismatch.
/// </summary>
/// <remarks>
/// This is the higher-value form of the check: extracting the shared facet block would keep the two calls
/// identical, but only an assertion over every field type × facet combination proves the two <em>resolved
/// store types</em> agree, which is the property the null projection actually depends on.
/// </remarks>
public sealed class SqliteStoreTypeAgreementTests
{
    [Theory]
    [InlineData(FieldType.Uuid, null, null, null)]
    [InlineData(FieldType.Ref, null, null, null)]
    [InlineData(FieldType.String, null, null, null)]
    [InlineData(FieldType.String, 32, null, null)]
    [InlineData(FieldType.String, 4000, null, null)]
    [InlineData(FieldType.Text, null, null, null)]
    [InlineData(FieldType.Enum, null, null, null)]
    [InlineData(FieldType.Json, null, null, null)]
    [InlineData(FieldType.Integer, null, null, null)]
    [InlineData(FieldType.Decimal, null, null, null)]
    [InlineData(FieldType.Decimal, null, 18, 2)]
    [InlineData(FieldType.Decimal, null, 10, 4)]
    [InlineData(FieldType.Decimal, null, 12, null)]
    [InlineData(FieldType.Boolean, null, null, null)]
    [InlineData(FieldType.Date, null, null, null)]
    [InlineData(FieldType.DateTime, null, null, null)]
    public void The_two_models_resolve_one_store_type_per_field_and_facet(
        FieldType type, int? maxLength, int? precision, int? scale)
    {
        var schema = SchemaWith(new FieldSchema
        {
            Name = "probe",
            Type = type,
            Nullable = true,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
        });

        ReadModelStoreType(schema).ShouldBe(MigrationModelStoreType(schema));
    }

    /// <summary>
    /// The masked-column mechanism end to end for one shape: the store type the read model resolves is what
    /// the dialect casts to, and it has to be a type this engine actually accepts in a <c>CAST</c>.
    /// </summary>
    [Fact]
    public void The_resolved_store_type_is_castable_by_this_engine()
    {
        var schema = SchemaWith(new FieldSchema
        {
            Name = "probe",
            Type = FieldType.Decimal,
            Nullable = true,
            Precision = 18,
            Scale = 2,
        });

        var projection = new SqliteSqlDialect().RenderNullProjection(ReadModelStoreType(schema));

        projection.ShouldBe($"CAST(NULL AS {ReadModelStoreType(schema)})");
    }

    /// <summary>
    /// The migration model's resolved store type. A stand-alone <see cref="ModelBuilder"/>'s model has to be
    /// runtime-initialized before a property will answer <c>GetColumnType()</c>, which is exactly what
    /// <c>EfCoreSchemaMigrator</c> does with the provider's own <see cref="IModelRuntimeInitializer"/> — so
    /// this reads the same type mapping the DDL was generated from, not a re-derived one.
    /// </summary>
    private static string MigrationModelStoreType(SchemaModel schema)
    {
        using var probe = new DbContext(SqliteOptions());
        var initializer = probe.GetInfrastructure().GetRequiredService<IModelRuntimeInitializer>();
        var model = initializer.Initialize(
            DescriptorModelBuilder.Build(schema, static () => new ModelBuilder(SqliteConventionSetBuilder.Build())));

        return model.FindEntityType("vehicle")!.FindProperty("probe")!.GetColumnType();
    }

    private static string ReadModelStoreType(SchemaModel schema)
    {
        using var context = new AlvoDataContext(SqliteOptions(), schema, Guid.NewGuid());
        return context.Model.FindEntityType("vehicle")!.FindProperty("probe")!.GetColumnType();
    }

    private static DbContextOptions SqliteOptions()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls());
        return options.Options;
    }

    private static SchemaModel SchemaWith(FieldSchema field) => new(
    [
        new EntitySchema
        {
            Name = "vehicle",
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }, field],
        },
    ]);
}
