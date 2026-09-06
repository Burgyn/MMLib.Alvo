#load "../_shared/Rows.csx"

using System.Text.Json;

// Create-or-replace (#105), against real PostgreSQL. The claims here are the ones only the real stack
// can carry: that a client-chosen id really becomes the row's primary key, that a replacement really
// drops a column the body omitted, and that a required-and-hidden field makes the entity un-replaceable
// for a caller who cannot read it back.

await tp.Test("A PUT on a free id creates the row under the id the client chose.", async () =>
{
    var body = await BodyOf(tp.Responses["ReplaceCreates"]);

    Equal("8f2a1c40-5d3e-4b91-9a77-2c6e0b4d1f01", body.GetProperty("id").GetString());
    Equal("Replaced into being", body.GetProperty("title").GetString());

    // The WHOLE value, not a Contains: "/api/work_orders/{id:guid}/<guid>" contains the id too, and
    // matches no route at all.
    var location = tp.Responses["ReplaceCreates"].Headers.Location?.ToString() ?? "";
    Equal("/api/work_orders/8f2a1c40-5d3e-4b91-9a77-2c6e0b4d1f01", location);
});

await tp.Test("The same PUT again replaces that row rather than creating a second.", async () =>
{
    var body = await BodyOf(tp.Responses["ReplaceReplaces"]);

    Equal("8f2a1c40-5d3e-4b91-9a77-2c6e0b4d1f01", body.GetProperty("id").GetString());
    Equal("Replaced again", body.GetProperty("title").GetString());

    // A 200 carries no Location. One that did would tell a client a row was created every time it was
    // merely replaced.
    Null(tp.Responses["ReplaceReplaces"].Headers.Location);
});

await tp.Test("A field the second body omitted is gone, not preserved.", async () =>
{
    var body = await BodyOf(tp.Responses["ReadReplaced"]);

    // Read back through GET rather than trusted from the PUT's own response: the claim is about what is
    // stored, and a route that answered its own echo would satisfy a weaker assertion.
    Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
    Equal("Replaced again", body.GetProperty("title").GetString());
});

await tp.Test("A required-and-hidden field makes the entity un-replaceable, and names itself.", async () =>
{
    var problem = await BodyOf(tp.Responses["ReplaceWithoutTheSecret"]);

    // Assert on the fields meant, not the whole body: the standalone host adds a traceId.
    Equal(422, problem.GetProperty("status").GetInt32());
    Contains("access_code", problem.ToString());
});

await tp.Test("PATCH with the same omission succeeds, so the refusal is about PUT and not the field.", async () =>
{
    var body = await BodyOf(tp.Responses["PatchWithoutTheSecret"]);

    Equal("Patched without the secret", body.GetProperty("title").GetString());
});
