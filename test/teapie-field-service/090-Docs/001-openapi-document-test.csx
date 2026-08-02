#load "../_shared/Rows.csx"

await tp.Test("The document is OpenAPI 3.1 and its paths are this descriptor's entities, and no others.", async () =>
{
    var document = await BodyOf(tp.Responses["Document"]);

    StartsWith("3.1", document.GetProperty("openapi").GetString());

    var paths = document.GetProperty("paths").EnumerateObject()
        .Select(path => path.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    Equal(
        new[]
        {
            "/api/customers", "/api/customers/{id}",
            "/api/regions", "/api/regions/{id}",
            "/api/work_orders", "/api/work_orders/{id}",
        },
        paths);
});

await tp.Test("Every entity's non-hidden fields are published on its row schema.", async () =>
{
    var document = await BodyOf(tp.Responses["Document"]);
    var schemas = document.GetProperty("components").GetProperty("schemas");

    var workOrder = schemas.GetProperty("work_orders").GetProperty("properties")
        .EnumerateObject().Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    Equal(
        new[]
        {
            "assigned_to", "completed_on", "contact_email", "created_at", "created_by",
            "customer_id", "description", "external_ref", "id", "is_emergency", "metadata",
            "priority", "quoted_price", "reference", "region_id", "scheduled_for", "status",
            "tenant_id", "title", "updated_at", "updated_by",
        },
        workOrder);

    var region = schemas.GetProperty("regions").GetProperty("properties")
        .EnumerateObject().Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
    Equal(new[] { "code", "id", "name" }, region);

    // A global entity carries no tenant_id, which is the schema-level half of the tenancy contrast.
    DoesNotContain("tenant_id", region);
    Contains("tenant_id", workOrder);
});

await tp.Test("An OPTIONAL hidden field's name appears nowhere in the whole document.", async () =>
{
    var raw = await tp.Responses["Document"].Content.ReadAsStringAsync();

    // Not "not in the row schema": nowhere. A name published in a request schema, a parameter, an
    // example or a description is published to the callers who may not read the field.
    DoesNotContain("internal_notes", raw);
});

await tp.Test("A REQUIRED hidden field's name appears in the write schemas only, and in no response schema.", async () =>
{
    var schemas = (await BodyOf(tp.Responses["Document"]))
        .GetProperty("components").GetProperty("schemas");

    static string[] Properties(System.Text.Json.JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

    // Published where a caller MUST read it to perform the create at all — a mandatory field nobody
    // was told about could not be supplied.
    Contains("access_code", Properties(schemas.GetProperty("work_ordersCreate")));
    Contains("access_code", Properties(schemas.GetProperty("work_ordersPatch")));
    Contains("access_code", schemas.GetProperty("work_ordersCreate").GetProperty("required")
        .EnumerateArray().Select(value => value.GetString()).ToArray());

    // And never where a caller could read a value back.
    DoesNotContain("access_code", Properties(schemas.GetProperty("work_orders")));
    DoesNotContain("access_code", Properties(schemas.GetProperty("work_ordersPageItem")));
});

await tp.Test("A readOnly field is the mirror image: in the response schemas, out of the write ones.", async () =>
{
    var schemas = (await BodyOf(tp.Responses["Document"]))
        .GetProperty("components").GetProperty("schemas");

    static string[] Properties(System.Text.Json.JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

    Contains("external_ref", Properties(schemas.GetProperty("work_orders")));
    DoesNotContain("external_ref", Properties(schemas.GetProperty("work_ordersCreate")));
    DoesNotContain("external_ref", Properties(schemas.GetProperty("work_ordersPatch")));

    // The response schema annotates it, so a generated client knows not to send it.
    True(schemas.GetProperty("work_orders").GetProperty("properties")
        .GetProperty("external_ref").GetProperty("readOnly").GetBoolean());
});

await tp.Test("A declared format publishes its PATTERN, anchored as Alvo enforces it; a built-in publishes its name.", async () =>
{
    var fields = (await BodyOf(tp.Responses["Document"]))
        .GetProperty("components").GetProperty("schemas")
        .GetProperty("work_orders").GetProperty("properties");

    // `work-order-ref` is the descriptor's own, so its name would mean nothing to a client — the
    // pattern is published instead, and with the anchors the API applies rather than the author's.
    Equal("^(?:WO-[0-9]{4,8})$", fields.GetProperty("reference").GetProperty("pattern").GetString());
    False(fields.GetProperty("reference").TryGetProperty("format", out _));

    // `email` is a built-in of a known vocabulary, so the name IS the useful thing.
    Equal("email", fields.GetProperty("contact_email").GetProperty("format").GetString());
});

await tp.Test("Each declared field type reaches its OpenAPI wire shape.", async () =>
{
    var fields = (await BodyOf(tp.Responses["Document"]))
        .GetProperty("components").GetProperty("schemas")
        .GetProperty("work_orders").GetProperty("properties");

    static string[] Types(System.Text.Json.JsonElement field)
    {
        var type = field.GetProperty("type");
        return type.ValueKind == System.Text.Json.JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()).OrderBy(v => v, StringComparer.Ordinal).ToArray()
            : new[] { type.GetString() };
    }

    Equal(new[] { "integer" }, Types(fields.GetProperty("priority")));
    Equal("int64", fields.GetProperty("priority").GetProperty("format").GetString());
    Equal(new[] { "null", "number" }, Types(fields.GetProperty("quoted_price")));
    Equal(new[] { "boolean", "null" }, Types(fields.GetProperty("is_emergency")));
    Equal(new[] { "null", "string" }, Types(fields.GetProperty("scheduled_for")));
    Equal("date-time", fields.GetProperty("scheduled_for").GetProperty("format").GetString());
    Equal("date", fields.GetProperty("completed_on").GetProperty("format").GetString());
    Equal("uuid", fields.GetProperty("customer_id").GetProperty("format").GetString());
    Equal(
        new[] { "cancelled", "completed", "in_progress", "scheduled" },
        fields.GetProperty("status").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).OrderBy(v => v, StringComparer.Ordinal).ToArray());
});

tp.Test("The docs UI is served from the container's own assets.", () =>
{
    Equal(200, (int)tp.Responses["Scalar"].StatusCode);
});
