using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which field names a row, which fields are the grid's columns, and what their headers say.
/// </summary>
public class GridColumnsTests
{
    [Fact]
    public void The_columns_are_the_label_two_enums_refs_amounts_dates_the_other_enums_and_the_rest()
    {
        var entity = Entity(
            Field("contact_email", FieldType.String),
            Field("payment_method", FieldType.Enum),
            Field("total", FieldType.Decimal, computed: "labour + parts"),
            Field("promised_on", FieldType.Date),
            Field("bike_id", FieldType.Ref),
            Field("status", FieldType.Enum, required: true),
            Field("priority", FieldType.Enum, required: true),
            Field("order_number", FieldType.String, required: true));

        GridColumns.Choose(entity, FieldMasks.None).Select(field => field.Name).ShouldBe(
            ["order_number", "payment_method", "status", "bike_id", "total", "promised_on", "priority"]);
    }

    [Fact]
    public void A_composite_label_leads_with_both_its_fields()
    {
        var entity = Entity(
            Field("tier", FieldType.Enum),
            Field("first_name", FieldType.String, required: true),
            Field("last_name", FieldType.String, required: true));

        GridColumns.Choose(entity, FieldMasks.None).Select(field => field.Name)
            .ShouldBe(["first_name", "last_name", "tier"]);
    }

    [Fact]
    public void The_columns_stop_at_the_cap()
    {
        var entity = Entity([.. Enumerable.Range(1, 12).Select(n => Field($"field_{n}", FieldType.Integer))]);

        GridColumns.Choose(entity, FieldMasks.None).Count.ShouldBe(GridColumns.Cap);
    }

    [Fact]
    public void Json_text_managed_and_always_hidden_fields_are_never_columns()
    {
        var entity = Entity(
            Field("id", FieldType.Uuid),
            Field("name", FieldType.String, required: true),
            Field("metadata", FieldType.Json),
            Field("notes", FieldType.Text),
            Field("internal_code", FieldType.String));

        GridColumns.Choose(entity, Masks(always: ["internal_code"])).Select(field => field.Name)
            .ShouldBe(["name"]);
    }

    [Fact]
    public void Rollups_are_columns()
    {
        var entity = Entity(Field("lines_count", FieldType.Integer, rollup: true));

        GridColumns.Choose(entity, FieldMasks.None).ShouldHaveSingleItem().Name.ShouldBe("lines_count");
    }

    [Theory]
    [InlineData("order_number", FieldType.String, "Order number")]
    [InlineData("bike_id", FieldType.Ref, "Bike")]
    [InlineData("assigned_user_id", FieldType.Ref, "Assigned user")]
    [InlineData("assigned_user_id", FieldType.Uuid, "Assigned user id")]
    [InlineData("status", FieldType.Enum, "Status")]
    [InlineData("id", FieldType.Ref, "Id")]
    [InlineData("API_key", FieldType.String, "Api key")]
    public void A_header_is_the_identifier_in_sentence_case(string name, FieldType type, string header)
        => GridColumns.Header(Field(name, type)).ShouldBe(header);

    private static FieldMasks Masks(string[]? always = null, string[]? conditional = null)
        => new(
            new HashSet<string>(always ?? [], StringComparer.Ordinal),
            new HashSet<string>(conditional ?? [], StringComparer.Ordinal));

    private static EntitySchema Entity(params FieldSchema[] fields)
        => new() { Name = "service_orders", Fields = fields };

    private static FieldSchema Field(
        string name, FieldType type, bool required = false, string? computed = null, bool rollup = false)
        => new()
        {
            Name = name,
            Type = type,
            Required = required,
            ComputedExpression = computed,
            Reference = type == FieldType.Ref ? new RefSchema("bikes", OnDelete.Restrict) : null,
            Rollup = rollup ? new RollupSchema { From = "order_lines", Op = RollupOperation.Count, Via = "order_id" } : null,
        };
}
