using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// HX-11 Ruling 73: a validation added or changed in the same apply that reads — directly or through a derived field — a
/// rollup whose definition that apply changes is refused when it is planned, at the validation's pointer, because the
/// CHECK would be judged against the values the old definition stored before the apply recomputes them.
/// </summary>
public sealed class RollupValidationOrderCheckTests
{
    private const string Held = "status == 'held'";
    private const string Released = "status == 'released'";

    [Fact]
    public void A_validation_added_over_a_rollup_the_apply_changes_is_refused_at_its_pointer()
    {
        var error = RollupValidationOrderCheck.Errors(
            Schema(where: Held), Schema(where: Released, reservedValidation: "value <= stock")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/products/fields/reserved/validation");
        error.Message.ShouldContain("'products.reserved'");
        error.FixSuggestion.ShouldBe("Apply the rollup change first, then the validation.");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
    }

    [Fact]
    public void A_validation_changed_over_a_rollup_the_apply_changes_is_refused()
    {
        RollupValidationOrderCheck.Errors(
            Schema(where: Held, reservedValidation: "value <= stock"),
            Schema(where: Released, reservedValidation: "stock >= value")).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_validation_reading_the_rollup_through_a_computed_field_is_refused_at_its_own_pointer()
    {
        var error = RollupValidationOrderCheck.Errors(
            Schema(where: Held, withFree: true),
            Schema(where: Released, withFree: true, stockValidation: "free >= 0")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/entities/products/fields/stock/validation");
        error.Message.ShouldContain("'products.reserved'");
    }

    [Fact]
    public void A_rollup_changed_alone_is_not_refused() =>
        RollupValidationOrderCheck.Errors(Schema(where: Held, reservedValidation: "value <= stock"), Schema(where: Released, reservedValidation: "value <= stock"))
            .ShouldBeEmpty();

    [Fact]
    public void A_validation_added_over_an_unchanged_rollup_is_not_refused() =>
        RollupValidationOrderCheck.Errors(Schema(where: Held), Schema(where: Held, reservedValidation: "value <= stock")).ShouldBeEmpty();

    [Fact]
    public void A_validation_that_reads_no_rollup_is_not_refused_beside_a_rollup_change() =>
        RollupValidationOrderCheck.Errors(Schema(where: Held), Schema(where: Released, stockValidation: "value >= 0")).ShouldBeEmpty();

    /// <summary>
    /// A rollup the apply adds holds no stored value before the backfill, so no CHECK can be judged against an old aggregate:
    /// the DDL admits the NULLs and the backfill's write is held to the constraint.
    /// </summary>
    [Fact]
    public void A_validation_over_a_rollup_the_same_apply_adds_is_not_refused() =>
        RollupValidationOrderCheck.Errors(Schema(where: null), Schema(where: Released, reservedValidation: "value <= stock")).ShouldBeEmpty();

    /// <summary>
    /// A stored plain field that becomes a rollup holds author-written values the backfill replaces, so it is a changed
    /// definition like any other (Ruling 76 amended) — only a brand-new rollup field is exempt.
    /// </summary>
    [Fact]
    public void A_plain_field_that_becomes_a_rollup_and_gains_a_validation_is_refused() =>
        RollupValidationOrderCheck.Errors(Schema(where: null, plainReserved: true), Schema(where: Released, reservedValidation: "value <= stock"))
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/products/fields/reserved/validation");

    [Fact]
    public void An_entity_the_apply_creates_is_not_refused() =>
        RollupValidationOrderCheck.Errors(new SchemaModel([]), Schema(where: Released, reservedValidation: "value <= stock")).ShouldBeEmpty();

    /// <summary>A renamed parent is matched through <c>renamedFrom</c>, so its changed rollup is still seen.</summary>
    [Fact]
    public void A_renamed_parent_s_changed_rollup_is_still_seen() =>
        RollupValidationOrderCheck.Errors(
            Schema(where: Held), Schema(where: Released, reservedValidation: "value <= stock", productsRenamedFrom: "products", products: "items"))
            .ShouldHaveSingleItem().Path.ShouldBe("/entities/items/fields/reserved/validation");

    private static SchemaModel Schema(
        string? where, string? reservedValidation = null, string? stockValidation = null, bool withFree = false,
        string products = "products", string? productsRenamedFrom = null, bool plainReserved = false)
    {
        var fields = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 },
            ["stock"] = Validated(new JsonObject { ["type"] = "integer" }, stockValidation),
        };
        if (where is not null)
        {
            fields["reserved"] = Validated(
                new JsonObject
                {
                    ["type"] = "integer",
                    ["rollup"] = new JsonObject { ["from"] = "reservations", ["op"] = "sum", ["field"] = "quantity", ["where"] = where },
                },
                reservedValidation);
        }

        if (plainReserved)
        {
            fields["reserved"] = new JsonObject { ["type"] = "integer" };
        }

        if (withFree)
        {
            fields["free"] = new JsonObject { ["type"] = "integer", ["computed"] = "stock - reserved" };
        }

        var parent = new JsonObject { ["fields"] = fields, ["rules"] = Rules() };
        if (productsRenamedFrom is not null)
        {
            parent["renamedFrom"] = productsRenamedFrom;
        }

        var descriptor = new JsonObject
        {
            ["$schema"] = "https://alvo.dev/schema/v1/project.json",
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "order",
            ["entities"] = new JsonObject
            {
                [products] = parent,
                ["reservations"] = new JsonObject
                {
                    ["fields"] = new JsonObject
                    {
                        ["product_id"] = new JsonObject { ["type"] = "ref", ["entity"] = products, ["required"] = true },
                        ["quantity"] = new JsonObject { ["type"] = "integer", ["required"] = true },
                        ["status"] = new JsonObject { ["type"] = "enum", ["values"] = new JsonArray("held", "released") },
                    },
                    ["rules"] = Rules(),
                },
            },
        };
        return DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptor.ToJsonString()));
    }

    private static JsonObject Validated(JsonObject field, string? validation)
    {
        if (validation is not null)
        {
            field["validation"] = validation;
        }

        return field;
    }

    private static JsonObject Rules() => new()
    {
        ["list"] = "true",
        ["get"] = "true",
        ["create"] = "true",
        ["update"] = "true",
        ["delete"] = "true",
    };
}
