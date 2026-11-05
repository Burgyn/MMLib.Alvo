using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using DescriptorValidationException = MMLib.Alvo.Descriptor.DescriptorValidationException;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The one name a field's CHECK constraint has (HX-11 Ruling 19): computed from the entity and field, always hashed, at
/// most 63 bytes, found again by recomputing it, and refused when two validated fields of one schema would share it.
/// </summary>
public sealed class CheckConstraintNamesTests
{
    [Fact]
    public void Every_name_carries_the_hash_of_entity_and_field() =>
        CheckConstraintNames.For("products", "stock").ShouldMatch("^ck_products_stock_[0-9a-f]{8}$");

    [Fact]
    public void The_hash_is_the_first_eight_hex_digits_of_sha256_over_entity_dot_field() =>
        CheckConstraintNames.For("products", "stock").ShouldEndWith(
            "_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("products.stock"u8))[..8]);

    [Fact]
    public void An_underscore_shared_between_entity_and_field_no_longer_collides() =>
        CheckConstraintNames.For("order_line", "total").ShouldNotBe(CheckConstraintNames.For("order", "line_total"));

    [Fact]
    public void A_name_is_stable_and_never_past_63_bytes()
    {
        var name = CheckConstraintNames.For(new string('e', 63), new string('f', 63));
        name.Length.ShouldBe(63);
        name.ShouldStartWith($"ck_{new string('e', 51)}_");
        name.ShouldBe(CheckConstraintNames.For(new string('e', 63), new string('f', 63)));
        name.ShouldNotBe(CheckConstraintNames.For(new string('e', 63), new string('f', 62) + "g"));
    }

    [Fact]
    public void A_readable_part_of_exactly_51_characters_is_kept_whole() =>
        CheckConstraintNames.For(new string('e', 25), new string('f', 25))
            .ShouldStartWith($"ck_{new string('e', 25)}_{new string('f', 25)}_");

    [Fact]
    public void The_owner_is_found_by_recomputing_the_written_entity_first() =>
        CheckConstraintNames.Owner(CheckSchemas.Products, CheckSchemas.Products.Entities[0], CheckConstraintNames.For("products", "stock"))!
            .Value.Field.Name.ShouldBe("stock");

    [Fact]
    public void A_parent_rolling_the_written_entity_up_is_searched_too()
    {
        var owner = CheckConstraintNames.Owner(CheckSchemas.Reserved, CheckSchemas.Reserved.Entities[1], CheckConstraintNames.For("products", "reserved"));

        owner.ShouldNotBeNull();
        (owner.Value.Entity.Name, owner.Value.Field.Name).ShouldBe(("products", "reserved"));
    }

    [Fact]
    public void A_name_no_validated_field_has_owns_nothing() =>
        CheckConstraintNames.Owner(CheckSchemas.Products, CheckSchemas.Products.Entities[0], CheckConstraintNames.For("products", "id"))
            .ShouldBeNull();

    [Fact]
    public void A_schema_whose_validated_fields_have_distinct_names_is_admitted() =>
        Should.NotThrow(() => CheckConstraintNames.EnsureDistinct(CheckSchemas.Reserved));

    [Fact]
    public void Two_validated_fields_sharing_a_name_are_refused_at_the_second_validation()
    {
        var refusal = Should.Throw<DescriptorValidationException>(() => CheckConstraintNames.EnsureDistinct(
            CheckSchemas.Products, (_, _) => "ck_same"));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/products/fields/discount/validation");
        error.Message.ShouldContain("products.stock");
        error.Message.ShouldContain("ck_same");
    }

    [Theory]
    [InlineData(true, "Adds the field's validation as a CHECK constraint.")]
    [InlineData(false, "Drops a field's validation.")]
    public void A_check_constraint_operation_is_a_non_destructive_alter_on_its_table(bool added, string detail)
    {
        MigrationOperation operation = added
            ? new AddCheckConstraintOperation { Table = "products", Name = "ck_x", Sql = "x" }
            : new DropCheckConstraintOperation { Table = "products", Name = "ck_x" };

        var change = DestructiveScan.Classify(operation);

        (change.Kind, change.Entity, change.IsDestructive, change.Detail).ShouldBe((SchemaChangeKind.AlterField, "products", false, detail));
    }

    private static class CheckSchemas
    {
        internal static SchemaModel Products { get; } = new([
            new EntitySchema
            {
                Name = "products",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = "stock >= 0" },
                    new FieldSchema { Name = "discount", Type = FieldType.Integer, Nullable = true, CheckExpression = "discount <= 50" },
                ],
            },
        ]);

        internal static SchemaModel Reserved { get; } = new([
            new EntitySchema
            {
                Name = "products",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "stock", Type = FieldType.Integer, Required = true },
                    new FieldSchema
                    {
                        Name = "reserved",
                        Type = FieldType.Integer,
                        Nullable = true,
                        CheckExpression = "reserved <= stock",
                        Rollup = new RollupSchema { From = "reservations", Op = RollupOperation.Sum, Field = "quantity", Via = "product_id" },
                    },
                ],
            },
            new EntitySchema
            {
                Name = "reservations",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "product_id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "quantity", Type = FieldType.Integer, Required = true, CheckExpression = "quantity > 0" },
                ],
            },
        ]);
    }
}
