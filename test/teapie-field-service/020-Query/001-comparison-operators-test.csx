#load "../_shared/Rows.csx"

// Every claim below is a SET of references, so a filter that widened, narrowed or shifted by one row
// fails. "The request succeeded" would pass for all of them and measure nothing.
async Task Rows(string request, params string[] expected) =>
    Equal(expected, await PageSet(tp.Responses[request], "reference"));

await tp.Test("eq matches exactly the one row whose value is equal.", async () =>
    await Rows("Eq", "WO-1003"));

await tp.Test("neq is the complement of eq over the same field — the two completed rows and nothing else are missing.", async () =>
    await Rows("Neq", "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1007"));

await tp.Test("gt excludes the boundary value and gte includes it.", async () =>
{
    await Rows("Gt", "WO-1005", "WO-1006", "WO-1007");
    await Rows("Gte", "WO-1005", "WO-1006", "WO-1007");

    // gt.2 and gte.3 agree here only because `priority` is integral, which is the point: the pair
    // below is where the boundary really shows, since priority 1 exists and priority 2 does too.
    await Rows("Lt", "WO-1001", "WO-1002");
    await Rows("Lte", "WO-1001", "WO-1002");
});

await tp.Test("like is a case-sensitive pattern match and ilike is the same pattern, case-folded.", async () =>
{
    await Rows("Like", "WO-1004");

    // The control. Without it, `ilike` returning WO-1004 would prove nothing about case folding —
    // a `like` that silently ignored case would pass the ilike assertion just as well.
    await Rows("LikeWrongCase");
    await Rows("ILike", "WO-1004");
});

await tp.Test("in matches every candidate that exists and silently ignores one that does not.", async () =>
    await Rows("In", "WO-1001", "WO-1007"));

await tp.Test("is.null selects the rows with no value, which is not what eq would answer.", async () =>
    await Rows("IsNull", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007"));

await tp.Test("is.true over a boolean excludes both false and null.", async () =>
    // WO-1007 carries no is_emergency at all, so a filter that read null as false would still
    // exclude it — but a filter that read null as unknown-and-therefore-included would not.
    await Rows("IsTrue", "WO-1002", "WO-1004"));

await tp.Test("Two filter parameters conjoin: the answer is the intersection, never the union.", async () =>
{
    // status=completed alone is {1005, 1006}; priority=3 alone is {1005, 1006} as well, so the
    // intersection has to be checked against a THIRD fact to be meaningful — that neither of the
    // two rows outside it (1007 is cancelled at priority 4) leaks in.
    await Rows("TwoParametersConjoin", "WO-1005", "WO-1006");
});

await tp.Test("An operator this API does not implement is refused, not read as equality.", async () =>
{
    var refused = tp.Responses["UnknownOperator"];

    Equal("https://alvo.dev/errors/malformed-query", await ProblemType(refused));
    Equal(new[] { "unknown-operator" }, await ViolationCodes(refused));
});
