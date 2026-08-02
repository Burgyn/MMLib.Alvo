#load "../_shared/Rows.csx"

await tp.Test("Capture: the work-order ids every later case references.", async () =>
{
    var wo1001 = await BodyOf(tp.Responses["CreateWo1001"]);
    var wo1004 = await BodyOf(tp.Responses["CreateWo1004"]);
    var wo1007 = await BodyOf(tp.Responses["CreateWo1007"]);

    tp.SetVariable("Wo1001Id", wo1001.GetProperty("id").GetString());
    tp.SetVariable("Wo1004Id", wo1004.GetProperty("id").GetString());
    tp.SetVariable("Wo1007Id", wo1007.GetProperty("id").GetString());
});

tp.Test("Every seeded work order was created, and each 201 carries a Location and a strong ETag.", () =>
{
    foreach (var name in new[]
    {
        "CreateWo1001", "CreateWo1002", "CreateWo1003", "CreateWo1004",
        "CreateWo1005", "CreateWo1006", "CreateWo1007",
    })
    {
        var created = tp.Responses[name];
        NotNull(created.Headers.Location);

        // `work_orders` declares audit: true, so every row has a version to tag. The sibling
        // assertion on `customers` says the opposite; one entity could not prove both.
        NotNull(created.Headers.ETag);
        False(created.Headers.ETag.IsWeak, $"{name} answered a weak ETag, which If-Match can never match.");
    }
});

await tp.Test("The framework stamped the audit columns with the caller, not with anything the payload said.", async () =>
{
    var row = await BodyOf(tp.Responses["CreateWo1001"]);

    Equal(Constant("userDispatcherNorth"), row.GetProperty("created_by").GetString());
    Equal(Constant("userDispatcherNorth"), row.GetProperty("updated_by").GetString());
    NotEqual(System.Text.Json.JsonValueKind.Null, row.GetProperty("created_at").ValueKind);
    NotEqual(System.Text.Json.JsonValueKind.Null, row.GetProperty("updated_at").ValueKind);
});

await tp.Test("Neither hidden field is echoed by the create that supplied it.", async () =>
{
    var keys = await KeysOf(tp.Responses["CreateWo1001"]);

    DoesNotContain("internal_notes", keys);
    DoesNotContain("access_code", keys);

    // The readOnly field is the control: it is absent from the WRITE schema but present in a
    // response, so "the row came back narrowed" cannot be what the two assertions above measured.
    Contains("external_ref", keys);
    Contains("assigned_to", keys);
});

await tp.Test("A json field is written as JSON and read back as the text of that JSON.", async () =>
{
    var row = await BodyOf(tp.Responses["CreateWo1001"]);
    var parsed = System.Text.Json.JsonDocument.Parse(row.GetProperty("metadata").GetString()).RootElement;

    Equal(3, parsed.GetProperty("floor").GetInt32());
    Equal("reception", parsed.GetProperty("keys_at").GetString());
});

await tp.Test("The remaining declared types survive the round trip at their declared shape.", async () =>
{
    var row = await BodyOf(tp.Responses["CreateWo1001"]);
    var cancelled = await BodyOf(tp.Responses["CreateWo1007"]);

    Equal(1250.50m, row.GetProperty("quoted_price").GetDecimal());
    Equal(1, row.GetProperty("priority").GetInt32());
    False(row.GetProperty("is_emergency").GetBoolean());
    Equal("scheduled", row.GetProperty("status").GetString());
    Equal(Constant("userTechNorth"), row.GetProperty("assigned_to").GetString());
    StartsWith("2026-09-01T08:00:00", row.GetProperty("scheduled_for").GetString());

    var completed = await BodyOf(tp.Responses["CreateWo1005"]);
    Equal("2026-07-14", completed.GetProperty("completed_on").GetString());

    // The row nobody is assigned and nothing was quoted for: a null is PRESENT as null, never absent.
    Equal(System.Text.Json.JsonValueKind.Null, cancelled.GetProperty("assigned_to").ValueKind);
    Equal(System.Text.Json.JsonValueKind.Null, cancelled.GetProperty("quoted_price").ValueKind);
    Equal(System.Text.Json.JsonValueKind.Null, cancelled.GetProperty("is_emergency").ValueKind);
});
