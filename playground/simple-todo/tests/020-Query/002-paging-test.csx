#load "../../../_shared/Rows.csx"

await tp.Test("Three pages of two, and the last one is short and closes the walk.", async () =>
{
    Equal(2, await PageCount(tp.Responses["Page1"]));
    Equal(2, await PageCount(tp.Responses["Page2"]));
    Equal(1, await PageCount(tp.Responses["Page3"]));

    NotNull(await NextCursor(tp.Responses["Page1"]));
    NotNull(await NextCursor(tp.Responses["Page2"]));

    // `next` is present and NULL on the last page rather than absent — that is the published contract,
    // and a client that had to tell "absent" from "null" could not page safely.
    Null(await NextCursor(tp.Responses["Page3"]));
});

await tp.Test("The walk visits every row exactly once, in the requested order.", async () =>
{
    var walked = new List<string>();
    foreach (var page in new[] { "Page1", "Page2", "Page3" })
    {
        walked.AddRange(await PageColumn(tp.Responses[page], "title"));
    }

    Equal(walked.Count, walked.Distinct(StringComparer.Ordinal).Count());

    // Compared with the WHOLE SET rather than with the pages' own union: three pages that consistently
    // lost the same row would agree with each other perfectly.
    Equal(await PageColumn(tp.Responses["WholeSet"], "title"), walked.ToArray());
});

await tp.Test("`offset` anchors the same window the cursor walk reached, from the other end.", async () =>
{
    var whole = await PageColumn(tp.Responses["WholeSet"], "title");

    Equal(whole.Skip(2).Take(2).ToArray(), await PageColumn(tp.Responses["OffsetWindow"], "title"));

    // Which is page 2 of the keyset walk, reached without a cursor. Both modes exist; neither is a
    // reimplementation of the other, and this is the one place they can be held to the same answer.
    Equal(await PageColumn(tp.Responses["Page2"], "title"), await PageColumn(tp.Responses["OffsetWindow"], "title"));
});
