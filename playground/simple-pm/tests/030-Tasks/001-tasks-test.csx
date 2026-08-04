#load "../../../_shared/Rows.csx"

using System.Text.Json;

// The five titles, built from the token so the expectations and the requests cannot disagree about
// which run's rows they mean.
string[] Titles(params string[] tails) =>
    tails.Select(tail => RunToken() + " " + tail).ToArray();

async Task Rows(string request, params string[] tails) =>
    Equal(Titles(tails), await PageColumn(tp.Responses[request], "title"));

await tp.Test("Capture: the task ids 002 moves through the board.", async () =>
{
    var b = await BodyOf(tp.Responses["TaskB"]);
    var d = await BodyOf(tp.Responses["TaskD"]);

    tp.SetVariable("TaskBId", b.GetProperty("id").GetString());
    tp.SetVariable("TaskDId", d.GetProperty("id").GetString());
});

await tp.Test("A task stores both references as the ids it was handed, and echoes them back.", async () =>
{
    var a = await BodyOf(tp.Responses["TaskA"]);

    Equal(Constant("AdaId"), a.GetProperty("assignee_id").GetString());
    Equal(Constant("BetaId"), a.GetProperty("milestone_id").GetString());
    Equal("Three entities, two references.", a.GetProperty("description").GetString());
});

await tp.Test("A decimal keeps the scale the field declares — 4.5 comes back at scale 2, not as 4.5.", async () =>
{
    var a = await BodyOf(tp.Responses["TaskA"]);
    var b = await BodyOf(tp.Responses["TaskB"]);

    // The claim is the VALUE, not its rendering: what a client does with 4.50 vs 4.5 is its own
    // business, but a store that lost the .5 would be a different number.
    Equal(4.5m, a.GetProperty("estimate_hours").GetDecimal());
    Equal(12m, b.GetProperty("estimate_hours").GetDecimal());
});

await tp.Test("Both references are optional, and an absent one is null rather than missing.", async () =>
{
    var unassigned = await BodyOf(tp.Responses["TaskUnassigned"]);

    Equal(JsonValueKind.Null, unassigned.GetProperty("assignee_id").ValueKind);
    Equal(JsonValueKind.Null, unassigned.GetProperty("milestone_id").ValueKind);
    Equal(JsonValueKind.Null, unassigned.GetProperty("estimate_hours").ValueKind);
});

await tp.Test("A filter over a `ref` field narrows to one milestone's tasks.", async () =>
{
    await Rows("AllTasks",
        "a design the schema", "b write the endpoints", "c review the endpoints",
        "d write the announcement", "e someone should look at this");

    // d is on the other milestone and e is on none — the two rows that make this a filter rather
    // than a read of everything.
    await Rows("BetaTasks", "a design the schema", "b write the endpoints", "c review the endpoints");
});

await tp.Test("Two terms conjoin: one person's open work is neither all their work nor all open work.", async () =>
{
    // Grace holds c and d, both open, so the conjunction is both of hers — and neither of Ada's,
    // whose `b` is also open. A build that dropped either term would answer three rows or four.
    await Rows("GraceOpenWork", "c review the endpoints", "d write the announcement");
});

await tp.Test("`is.null` on a `ref` is how the triage view is built.", async () =>
    await Rows("Unassigned", "e someone should look at this"));

await tp.Test("A reference to a row that does not exist is refused BEFORE the write, naming the field.", async () =>
{
    var refused = tp.Responses["AssigneeDoesNotExist"];

    // `unresolved-reference`, and 422 rather than 409: ref existence is a facet the framework checks
    // itself, unlike a `unique` collision, which only the engine can answer.
    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
    Equal(new[] { "unresolved-reference" }, await ViolationCodes(refused));
    Equal(new[] { "/assignee_id" }, await ViolationPointers(refused));
});

await tp.Test("A value past the declared scale is refused rather than silently rounded.", async () =>
{
    var refused = tp.Responses["TooPrecise"];

    Equal(new[] { "scale" }, await ViolationCodes(refused));
    Equal(new[] { "/estimate_hours" }, await ViolationPointers(refused));
});
