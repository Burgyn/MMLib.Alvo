#load "../_shared/Rows.csx"

async Task<string[]> Walk(params string[] pages)
{
    var walked = new List<string>();
    foreach (var page in pages)
    {
        walked.AddRange(await PageColumn(tp.Responses[page], "reference"));
    }

    return walked.ToArray();
}

await tp.Test("A nullable sort key pages to exhaustion under `nullslast`, losing no row and repeating none.", async () =>
{
    var walked = await Walk("LastPage1", "LastPage2", "LastPage3", "LastPage4");
    var whole = await PageColumn(tp.Responses["LastWholeSet"], "reference");

    Equal(7, whole.Length);
    Equal(walked.Length, walked.Distinct(StringComparer.Ordinal).Count());

    // Compared with the WHOLE SET, not with the pages' own union: a walk that consistently lost the
    // same row would agree with itself perfectly. This is the direct inverse of the measured defect —
    // the null-keyed tail used to be unreachable, so the walk ended after the two dated rows.
    Equal(whole, walked);
});

await tp.Test("The two dated rows lead the `nullslast` order and the five null-keyed ones follow.", async () =>
{
    var whole = await PageColumn(tp.Responses["LastWholeSet"], "reference");

    Equal(new[] { "WO-1001", "WO-1002" }, whole.Take(2).ToArray());

    var body = await BodyOf(tp.Responses["LastWholeSet"]);
    var tail = body.GetProperty("items").EnumerateArray().Skip(2);
    True(tail.All(row => row.GetProperty("scheduled_for").ValueKind == System.Text.Json.JsonValueKind.Null));
});

await tp.Test("Under `nullsfirst` the first page's own anchor is a null-keyed row, and the walk still completes.", async () =>
{
    // The sharper of the two defects: page one ended on a null-keyed row, so page two's cursor
    // anchored on a NULL and matched nothing at all — an empty page two, silently.
    var page1 = await BodyOf(tp.Responses["FirstPage1"]);
    True(page1.GetProperty("items").EnumerateArray()
        .All(row => row.GetProperty("scheduled_for").ValueKind == System.Text.Json.JsonValueKind.Null));

    Equal(2, await PageCount(tp.Responses["FirstPage2"]));

    var walked = await Walk("FirstPage1", "FirstPage2", "FirstPage3", "FirstPage4");
    Equal(await PageColumn(tp.Responses["FirstWholeSet"], "reference"), walked);
});

await tp.Test("The two placements really do differ, so neither walk passes by ignoring the modifier.", async () =>
{
    var last = await PageColumn(tp.Responses["LastWholeSet"], "reference");
    var first = await PageColumn(tp.Responses["FirstWholeSet"], "reference");

    NotEqual(last, first);
    Equal(
        last.OrderBy(reference => reference, StringComparer.Ordinal).ToArray(),
        first.OrderBy(reference => reference, StringComparer.Ordinal).ToArray());
});
