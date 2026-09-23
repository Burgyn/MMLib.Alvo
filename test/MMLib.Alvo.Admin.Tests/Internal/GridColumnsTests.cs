using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which field names a row, which fields are the grid's columns, and what their headers say.
/// </summary>
public class GridColumnsTests
{
    [Fact]
    public void The_label_is_the_first_required_string()
    {
        var entity = Entity(
            Field("nickname", FieldType.String),
            Field("status", FieldType.Enum, required: true),
            Field("order_number", FieldType.String, required: true),
            Field("title", FieldType.String, required: true));

        GridColumns.LabelField(entity, FieldMasks.None)!.Name.ShouldBe("order_number");
    }

    [Fact]
    public void Without_a_required_string_the_label_is_the_first_string()
    {
        var entity = Entity(
            Field("amount", FieldType.Decimal, required: true),
            Field("nickname", FieldType.String),
            Field("colour", FieldType.String));

        GridColumns.LabelField(entity, FieldMasks.None)!.Name.ShouldBe("nickname");
    }

    [Fact]
    public void An_entity_with_no_string_has_no_label()
        => GridColumns.LabelField(
            Entity(Field("amount", FieldType.Decimal), Field("notes", FieldType.Text)), FieldMasks.None)
            .ShouldBeNull("a text field is prose, not a name");

    [Fact]
    public void A_field_hidden_from_everyone_is_never_the_label()
    {
        var entity = Entity(
            Field("access_code", FieldType.String, required: true),
            Field("reference", FieldType.String, required: true));

        GridColumns.LabelField(entity, Masks(always: ["access_code"]))!.Name.ShouldBe(
            "reference", "a field that never comes back would label every row with the short id");
    }

    [Fact]
    public void A_field_masked_by_cel_can_still_be_the_label()
    {
        var entity = Entity(Field("full_name", FieldType.String, required: true));

        GridColumns.LabelField(entity, Masks(conditional: ["full_name"]))!.Name.ShouldBe(
            "full_name", "it comes back for some callers, and the others get the short id");
    }

    [Fact]
    public void The_columns_are_the_label_then_enums_refs_dates_amounts_and_the_rest()
    {
        var entity = Entity(
            Field("contact_email", FieldType.String),
            Field("total", FieldType.Decimal, computed: "labour + parts"),
            Field("promised_on", FieldType.Date),
            Field("bike_id", FieldType.Ref),
            Field("status", FieldType.Enum, required: true),
            Field("order_number", FieldType.String, required: true),
            Field("paid", FieldType.Boolean));

        GridColumns.Choose(entity, FieldMasks.None).Select(field => field.Name).ShouldBe(
            ["order_number", "status", "bike_id", "promised_on", "total", "contact_email", "paid"]);
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
