using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class DescriptorModelBuilderTests
{
    private static ModelBuilder NewSqliteBuilder() => new(SqliteConventionSetBuilder.Build());

    [Fact]
    public void Builds_entity_with_key_and_required_property()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var entityType = efModel.FindEntityType("vehicles")!;
        entityType.FindPrimaryKey()!.Properties.Single().Name.ShouldBe("id");
        entityType.FindProperty("vin")!.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void Nullable_field_produces_a_nullable_property()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "nickname", Type = FieldType.String, Nullable = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var entityType = efModel.FindEntityType("vehicles")!;
        entityType.FindProperty("nickname")!.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_nullable_true_overrides_required_true()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, Required = true, Nullable = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        efModel.FindEntityType("vehicles")!.FindProperty("vin")!.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_nullable_false_overrides_required_false()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, Required = false, Nullable = false },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        efModel.FindEntityType("vehicles")!.FindProperty("vin")!.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void Ref_field_produces_a_foreign_key_to_the_target_entity()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                ],
            },
            new EntitySchema
            {
                Name = "orders",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema
                    {
                        Name = "vehicle_id",
                        Type = FieldType.Ref,
                        Required = true,
                        Reference = new RefSchema("vehicles", OnDelete.Cascade),
                    },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var orders = efModel.FindEntityType("orders")!;
        var foreignKey = orders.GetForeignKeys().Single();
        foreignKey.PrincipalEntityType.Name.ShouldBe("vehicles");
        foreignKey.Properties.Single().Name.ShouldBe("vehicle_id");
        foreignKey.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    [Fact]
    public void Ref_field_with_missing_target_entity_keeps_the_column_but_skips_the_foreign_key()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "orders",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema
                    {
                        Name = "vehicle_id",
                        Type = FieldType.Ref,
                        Nullable = true,
                        Reference = new RefSchema("vehicles", OnDelete.Restrict),
                    },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var orders = efModel.FindEntityType("orders")!;
        orders.GetForeignKeys().ShouldBeEmpty();
        orders.FindProperty("vehicle_id")!.ClrType.ShouldBe(typeof(Guid?));
    }

    [Fact]
    public void Decimal_field_carries_precision_and_scale()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "price", Type = FieldType.Decimal, Required = true, Precision = 18, Scale = 2 },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var price = efModel.FindEntityType("vehicles")!.FindProperty("price")!;
        price.GetPrecision().ShouldBe(18);
        price.GetScale().ShouldBe(2);
    }

    [Fact]
    public void Unique_field_produces_a_unique_index()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, Required = true, Unique = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var vehicles = efModel.FindEntityType("vehicles")!;
        var index = vehicles.GetIndexes().Single(i => i.Properties.Single().Name == "vin");
        index.IsUnique.ShouldBeTrue();
    }

    /// <summary>
    /// #137. A <c>unique</c> field on a <c>tenancy: "scoped"</c> entity must be unique <em>within</em> the
    /// tenant, never across the instance: an instance-wide constraint answers tenant B a question about
    /// tenant A's data — whether it holds a given value — one create per candidate, which is a cross-tenant
    /// existence oracle and not merely a modelling nit.
    /// </summary>
    [Fact]
    public void Unique_field_on_a_scoped_entity_is_scoped_to_the_tenant()
    {
        IModel efModel = DescriptorModelBuilder.Build(ScopedVehicles(), NewSqliteBuilder);

        var index = efModel.FindEntityType("vehicles")!.GetIndexes().Single(i => i.IsUnique);
        index.Properties.Select(p => p.Name).ShouldBe(["tenant_id", "vin"]);
    }

    /// <summary>
    /// #137's other half: the constraint must still <b>hold</b>. Scoping it to the tenant is only correct if
    /// two rows in one tenant still collide, so the index has to be unique over the pair rather than over
    /// <c>tenant_id</c> alone or a non-unique index on both.
    /// </summary>
    [Fact]
    public void The_tenant_scoped_unique_index_is_still_unique()
    {
        IModel efModel = DescriptorModelBuilder.Build(ScopedVehicles(), NewSqliteBuilder);

        var index = efModel.FindEntityType("vehicles")!.GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual(["tenant_id", "vin"]));
        index.IsUnique.ShouldBeTrue();
    }

    /// <summary>
    /// A non-scoped entity keeps instance-wide uniqueness. Tenancy is what narrows the constraint, so an
    /// entity with none must be left exactly as it was — the fix must not weaken uniqueness where there is
    /// no tenant boundary to respect.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(TenancyMode.Global)]
    public void Unique_field_without_scoped_tenancy_stays_unique_instance_wide(TenancyMode? tenancy)
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Tenancy = tenancy,
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "vin", Type = FieldType.String, Required = true, Unique = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var index = efModel.FindEntityType("vehicles")!.GetIndexes().Single(i => i.IsUnique);
        index.Properties.Select(p => p.Name).ShouldBe(["vin"]);
    }

    /// <summary>
    /// #137's second site. The issue text named only the per-field emission; a <b>declared</b> unique index
    /// (<c>indexes: [{ "fields": [...], "unique": true }]</c>) is the identical oracle and was omitted, so
    /// both are asserted rather than one.
    /// </summary>
    [Fact]
    public void Declared_unique_index_on_a_scoped_entity_is_scoped_to_the_tenant()
    {
        IModel efModel = DescriptorModelBuilder.Build(
            ScopedVehicles([new IndexSchema(["make", "model"], true)]), NewSqliteBuilder);

        var index = efModel.FindEntityType("vehicles")!.GetIndexes().Single(i => i.Properties.Count == 3);
        index.IsUnique.ShouldBeTrue();
        index.Properties.Select(p => p.Name).ShouldBe(["tenant_id", "make", "model"]);
    }

    /// <summary>
    /// A declared <em>non-unique</em> index enforces nothing, so it discloses nothing and is left alone —
    /// prefixing it would change emitted DDL for no security gain, and an index's column order is the
    /// author's own performance decision.
    /// </summary>
    [Fact]
    public void Declared_non_unique_index_on_a_scoped_entity_is_left_alone()
    {
        IModel efModel = DescriptorModelBuilder.Build(
            ScopedVehicles([new IndexSchema(["make", "model"], false)]), NewSqliteBuilder);

        var index = efModel.FindEntityType("vehicles")!.GetIndexes().Single(i => !i.IsUnique);
        index.Properties.Select(p => p.Name).ShouldBe(["make", "model"]);
    }

    /// <summary>
    /// A descriptor that already named <c>tenant_id</c> in its own unique index gets it once, not twice: EF
    /// refuses an index naming one property twice, so an unconditional prepend would turn a legal descriptor
    /// into a startup crash.
    /// </summary>
    [Fact]
    public void Declared_unique_index_already_naming_the_tenant_is_not_doubled()
    {
        IModel efModel = DescriptorModelBuilder.Build(
            ScopedVehicles([new IndexSchema(["tenant_id", "make"], true)]), NewSqliteBuilder);

        var columns = efModel.FindEntityType("vehicles")!.GetIndexes()
            .Select(i => i.Properties.Select(p => p.Name).ToArray());
        columns.ShouldContain(names => names.SequenceEqual(new[] { "tenant_id", "make" }));
    }

    /// <summary>
    /// The fixture #137's facts are measured against: a scoped entity whose <c>tenant_id</c> is declared
    /// <b>after</b> the unique field, exactly as <c>DescriptorToSchemaMapper</c> appends its managed columns.
    /// That ordering is the reason the emission cannot stay in the per-field loop.
    /// </summary>
    private static SchemaModel ScopedVehicles(IReadOnlyList<IndexSchema>? indexes = null) => new([
        new EntitySchema
        {
            Name = "vehicles",
            Tenancy = TenancyMode.Scoped,
            Fields = [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "vin", Type = FieldType.String, Required = true, Unique = true },
                new FieldSchema { Name = "make", Type = FieldType.String, Required = true },
                new FieldSchema { Name = "model", Type = FieldType.String, Required = true },
                new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true },
            ],
            Indexes = indexes ?? [],
        },
    ]);

    [Fact]
    public void Indexed_field_produces_a_non_unique_index()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "make", Type = FieldType.String, Required = true, Indexed = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var vehicles = efModel.FindEntityType("vehicles")!;
        var index = vehicles.GetIndexes().Single(i => i.Properties.Single().Name == "make");
        index.IsUnique.ShouldBeFalse();
    }

    [Fact]
    public void Entity_level_index_produces_a_composite_index()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "make", Type = FieldType.String, Required = true },
                    new FieldSchema { Name = "model", Type = FieldType.String, Required = true },
                ],
                Indexes = [new IndexSchema(["make", "model"], true)],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var vehicles = efModel.FindEntityType("vehicles")!;
        var index = vehicles.GetIndexes().Single(i => i.Properties.Count == 2);
        index.IsUnique.ShouldBeTrue();
        index.Properties.Select(p => p.Name).ShouldBe(["make", "model"]);
    }

    [Fact]
    public void Computed_expression_is_ignored_until_the_cel_sql_compiler_lands()
    {
        // #20: the raw descriptor-string -> GENERATED ALWAYS AS (...) STORED splice was an
        // arbitrary-DDL-injection vector, so the builder no longer honors ComputedExpression.
        // DescriptorToSchemaMapper already refuses 'computed' at mapping time (#21 revives this).
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "make", Type = FieldType.String, Required = true },
                    new FieldSchema { Name = "model", Type = FieldType.String, Required = true },
                    new FieldSchema
                    {
                        Name = "full_name",
                        Type = FieldType.String,
                        ComputedExpression = "make || ' ' || model",
                    },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var fullName = efModel.FindEntityType("vehicles")!.FindProperty("full_name")!;
        fullName.GetComputedColumnSql().ShouldBeNull();
        fullName.GetIsStored().ShouldBeNull();
    }

    [Theory]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Json)]
    [InlineData(FieldType.Enum)]
    public void String_backed_field_types_map_to_string(FieldType type)
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "value", Type = type, Required = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        efModel.FindEntityType("vehicles")!.FindProperty("value")!.ClrType.ShouldBe(typeof(string));
    }

    [Fact]
    public void Integer_field_maps_to_long()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "mileage", Type = FieldType.Integer, Required = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        efModel.FindEntityType("vehicles")!.FindProperty("mileage")!.ClrType.ShouldBe(typeof(long));
    }

    [Fact]
    public void Boolean_field_maps_to_bool()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "is_active", Type = FieldType.Boolean, Required = true },
                    new FieldSchema { Name = "is_scrapped", Type = FieldType.Boolean, Nullable = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var entityType = efModel.FindEntityType("vehicles")!;
        var isActive = entityType.FindProperty("is_active")!;
        isActive.ClrType.ShouldBe(typeof(bool));
        isActive.IsNullable.ShouldBeFalse();

        var isScrapped = entityType.FindProperty("is_scrapped")!;
        isScrapped.ClrType.ShouldBe(typeof(bool?));
        isScrapped.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void Date_and_datetime_fields_map_to_DateOnly_and_DateTimeOffset()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "manufactured_on", Type = FieldType.Date, Required = true },
                    new FieldSchema { Name = "registered_at", Type = FieldType.DateTime, Required = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        var entityType = efModel.FindEntityType("vehicles")!;
        entityType.FindProperty("manufactured_on")!.ClrType.ShouldBe(typeof(DateOnly));
        entityType.FindProperty("registered_at")!.ClrType.ShouldBe(typeof(DateTimeOffset));
    }

    [Fact]
    public void Table_name_matches_entity_name()
    {
        var model = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields = [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                ],
            },
        ]);

        IModel efModel = DescriptorModelBuilder.Build(model, NewSqliteBuilder);

        efModel.FindEntityType("vehicles")!.GetTableName().ShouldBe("vehicles");
    }
}
