#load "../../../_shared/Rows.csx"

await tp.Test("Capture: the two milestone ids the task cases reference.", async () =>
{
    var beta = await BodyOf(tp.Responses["CreateBeta"]);
    var launch = await BodyOf(tp.Responses["CreateLaunch"]);

    tp.SetVariable("BetaId", beta.GetProperty("id").GetString());
    tp.SetVariable("LaunchId", launch.GetProperty("id").GetString());
});

await tp.Test("A milestone carries the three declared fields it was sent.", async () =>
{
    var beta = await BodyOf(tp.Responses["CreateBeta"]);

    Equal(RunToken() + " Beta", beta.GetProperty("name").GetString());
    Equal("active", beta.GetProperty("status").GetString());
    Equal("2026-09-30", beta.GetProperty("due_on").GetString());
});

await tp.Test("A create hands out a version, so the first write of a row needs no read before it.", async () =>
{
    var onCreate = tp.Responses["CreateBeta"].Headers.ETag;

    NotNull(onCreate);
    False(onCreate.IsWeak);

    // And the conditional PATCH that used it was accepted, which is the half that matters: a tag
    // that was minted but not comparable would fail the If-Match rather than satisfy it.
    var advanced = await BodyOf(tp.Responses["AdvanceBeta"]);
    Equal("done", advanced.GetProperty("status").GetString());

    // The unmentioned fields survived the partial update.
    Equal(RunToken() + " Beta", advanced.GetProperty("name").GetString());
    Equal("2026-09-30", advanced.GetProperty("due_on").GetString());
});

await tp.Test("An undeclared enum value is refused, and the fix lists the values that exist.", async () =>
{
    var refused = tp.Responses["NotADeclaredStatus"];

    Equal(new[] { "enum-value" }, await ViolationCodes(refused));
    Equal(new[] { "/status" }, await ViolationPointers(refused));

    var body = await BodyOf(refused);
    Contains("planned, active, done",
        body.GetProperty("violations")[0].GetProperty("fixSuggestion").GetString());
});

await tp.Test("The plan is the two accepted milestones; the refused one left no row.", async () =>
    Equal(
        new[] { RunToken() + " Beta", RunToken() + " Launch" },
        await PageColumn(tp.Responses["Plan"], "name")));
