#load "../_shared/Rows.csx"

// The capture step comes first and asserts NOTHING, deliberately. A `tp.SetVariable` inside a failing
// test never runs, so folding the capture into an assertion would let one wrong expectation here take
// out every later test case with "Variable 'RegionNorthId' was not found" instead of reporting itself.
// It cannot live at the script's top level either: TeaPie runs a script's top-level body BEFORE the
// requests and only defers the `tp.Test` bodies, so `tp.Responses` is empty up there.
await tp.Test("Capture: the two region ids every later case references.", async () =>
{
    var north = await BodyOf(tp.Responses["CreateNorthRegion"]);
    var central = await BodyOf(tp.Responses["CreateCentralRegion"]);

    tp.SetVariable("RegionNorthId", north.GetProperty("id").GetString());
    tp.SetVariable("RegionCentralId", central.GetProperty("id").GetString());
});

await tp.Test("A region is created by the admin key and carries what it was sent.", async () =>
{
    var north = await BodyOf(tp.Responses["CreateNorthRegion"]);
    var central = await BodyOf(tp.Responses["CreateCentralRegion"]);

    Equal("NORTH", north.GetProperty("code").GetString());
    Equal("Northern region", north.GetProperty("name").GetString());
    Equal("CENTRAL", central.GetProperty("code").GetString());
});

tp.Test("`regions` is not audited, so a created region carries no ETag to condition a write on.", () =>
{
    Null(tp.Responses["CreateNorthRegion"].Headers.ETag);
});

await tp.Test("The dispatcher's refusal is the create rule rejecting the row, not a missing rule.", async () =>
{
    var refused = tp.Responses["DispatcherCannotCreateRegion"];

    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));

    // The distinction 070-Authorization measures: `regions.create` IS configured, so a caller who
    // fails it reaches an allow decision whose WITH CHECK rejects the candidate row. An operation
    // with no rule at all answers "No policy allows '<op>' on this entity." instead — which is what
    // DELETE /api/regions/{id} answers, and what makes the two shapes distinguishable.
    var body = await BodyOf(refused);
    Equal("The write was rejected by policy.", body.GetProperty("detail").GetString());
});
