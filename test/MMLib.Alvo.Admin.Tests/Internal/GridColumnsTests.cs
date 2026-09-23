using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which field names a row, which fields are the grid's columns, and what their headers say.
/// </summary>
public class GridColumnsTests
{
    [Fact]
    public void A_service_order_shows_its_number_state_bike_technician_total_and_promise()
    {
        var columns = GridColumns.Choose(ServiceOrders(), FieldMasks.None).Select(field => field.Name).ToList();

        columns.Count.ShouldBe(GridColumns.Cap);
        columns.Take(7).ShouldBe(
            ["order_number", "status", "priority", "bike_id", "technician_id", "total", "promised_on"],
            "label, two enums, two refs, the one amount, the one date");
        columns[7].ShouldBe("labour_hours", "the rest fill as amounts before dates and enums");
    }

    [Theory]
    [InlineData(new[] { "labour_total", "amount", "total" }, "total")]
    [InlineData(new[] { "grand_total", "amount" }, "amount")]
    [InlineData(new[] { "net", "grand_total", "labour_total" }, "grand_total")]
    [InlineData(new[] { "labour_total", "parts_total", "net" }, "parts_total")]
    [InlineData(new[] { "net", "gross" }, "net")]
    public void The_one_amount_is_the_most_total_like_decimal(string[] decimals, string amount)
    {
        var entity = Entity([.. decimals.Select(name => Field(name, FieldType.Decimal))]);

        GridColumns.Choose(entity, FieldMasks.None)[0].Name.ShouldBe(amount);
    }

    [Fact]
    public void Without_a_total_like_name_the_one_amount_is_the_last_derived_decimal()
    {
        var entity = Entity(
            Field("rate", FieldType.Decimal),
            Field("margin", FieldType.Decimal, computed: "price - cost"),
            Field("revenue", FieldType.Decimal, rollup: true),
            Field("cost", FieldType.Decimal));

        GridColumns.Choose(entity, FieldMasks.None)[0].Name.ShouldBe("revenue");
    }

    [Theory]
    [InlineData(new[] { "received_at", "promised_on" }, "promised_on")]
    [InlineData(new[] { "birthday", "starts_at", "due_at" }, "starts_at")]
    [InlineData(new[] { "birthday", "invoice_date" }, "invoice_date")]
    [InlineData(new[] { "birthday", "deadline" }, "deadline")]
    [InlineData(new[] { "birthday", "anniversary" }, "birthday")]
    public void The_one_date_is_the_first_by_suffix_then_by_name_then_the_first(string[] dates, string date)
    {
        var entity = Entity([.. dates.Select(name => Field(name, FieldType.Date))]);

        GridColumns.Choose(entity, FieldMasks.None)[0].Name.ShouldBe(date);
    }

    [Fact]
    public void An_audit_column_is_never_the_date()
    {
        var fields = new[]
        {
            Field("created_at", FieldType.DateTime),
            Field("updated_at", FieldType.DateTime),
            Field("birthday", FieldType.Date),
        };
        var entity = new EntitySchema { Name = "customers", Audit = true, Fields = fields };

        GridColumns.Choose(entity, FieldMasks.None).Select(field => field.Name).ShouldBe(["birthday"]);
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

    /// <summary>bike-workshop's <c>service_orders</c>, field for field.</summary>
    private static EntitySchema ServiceOrders() => Entity(
        Field("order_number", FieldType.String, required: true),
        Field("bike_id", FieldType.Ref, required: true),
        Field("technician_id", FieldType.Ref),
        Field("assigned_user_id", FieldType.Uuid),
        Field("status", FieldType.Enum, required: true),
        Field("priority", FieldType.Enum, required: true),
        Field("service_type", FieldType.Enum, required: true),
        Field("received_at", FieldType.DateTime, required: true),
        Field("promised_on", FieldType.Date),
        Field("completed_at", FieldType.DateTime),
        Field("problem_description", FieldType.Text, required: true),
        Field("work_notes", FieldType.Text),
        Field("contact_email", FieldType.String),
        Field("notify_customer", FieldType.Boolean),
        Field("labour_hours", FieldType.Decimal),
        Field("labour_rate", FieldType.Decimal),
        Field("labour_total", FieldType.Decimal, computed: "labour_hours * labour_rate"),
        Field("parts_total", FieldType.Decimal, rollup: true),
        Field("lines_count", FieldType.Integer, rollup: true),
        Field("total", FieldType.Decimal, computed: "labour_total + parts_total"),
        Field("paid", FieldType.Boolean),
        Field("payment_method", FieldType.Enum),
        Field("diagnostics", FieldType.Json));

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
