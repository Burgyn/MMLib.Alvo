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
    public void Computed_field_produces_a_stored_computed_column()
    {
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
        fullName.GetComputedColumnSql().ShouldBe("make || ' ' || model");
        fullName.GetIsStored().ShouldBe(true);
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
