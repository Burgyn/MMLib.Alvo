#load "../_shared/Rows.csx"

await tp.Test("Four pages of two, and the last one is short and closes the walk.", async () =>
{
    Equal(2, await PageCount(tp.Responses["Page1"]));
    Equal(2, await PageCount(tp.Responses["Page2"]));
    Equal(2, await PageCount(tp.Responses["Page3"]));
    Equal(1, await PageCount(tp.Responses["Page4"]));

    NotNull(await NextCursor(tp.Responses["Page1"]));
    NotNull(await NextCursor(tp.Responses["Page2"]));
    NotNull(await NextCursor(tp.Responses["Page3"]));

    // `next` is present and null on the last page rather than absent — that is the published
    // contract, and a client that had to distinguish "absent" from "null" could not page safely.
    Null(await NextCursor(tp.Responses["Page4"]));
});

await tp.Test("The walk visits every row exactly once — no duplicate across a boundary, no row missed.", async () =>
{
    var walked = new List<string>();
    foreach (var page in new[] { "Page1", "Page2", "Page3", "Page4" })
    {
        walked.AddRange(await PageColumn(tp.Responses[page], "id"));
    }

    var whole = await PageColumn(tp.Responses["WholeSet"], "id");

    Equal(7, whole.Length);
    Equal(walked.Count, walked.Distinct(StringComparer.Ordinal).Count());

    // Compared with the WHOLE SET rather than with the pages' own union: four pages that
    // consistently lost the same row would agree with each other perfectly.
    Equal(whole.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
          walked.OrderBy(id => id, StringComparer.Ordinal).ToArray());
});

await tp.Test("The walk is in the requested order, tie-broken consistently — page N+1 continues page N.", async () =>
{
    var walked = new List<string>();
    foreach (var page in new[] { "Page1", "Page2", "Page3", "Page4" })
    {
        walked.AddRange(await PageColumn(tp.Responses[page], "reference"));
    }

    // Identical to the single-request read of the same query. Three ties on `priority` mean the
    // sequence is decided by the id tie-breaker, so an implementation that dropped it would order
    // the tied pairs differently here than in the unpaged read.
    Equal(await PageColumn(tp.Responses["WholeSet"], "reference"), walked.ToArray());
});

await tp.Test("Offset paging anchors the same window the cursor walk reached, from the other end.", async () =>
{
    var offsetPage = await PageColumn(tp.Responses["OffsetPaging"], "reference");
    var whole = await PageColumn(tp.Responses["WholeSet"], "reference");

    Equal(whole.Skip(5).Take(2).ToArray(), offsetPage);
});

await tp.Test("A cursor no page could have issued is refused rather than answered with the first page.", async () =>
{
    var refused = tp.Responses["ForgedCursor"];

    Equal(new[] { "invalid-cursor" }, await ViolationCodes(refused));
    Equal(new[] { "after" }, await ViolationPointers(refused));
});

await tp.Test("A request anchoring one window two ways is refused rather than served by whichever won.", async () =>
    Equal(new[] { "conflicting-paging" }, await ViolationCodes(tp.Responses["CursorAndOffset"])));

await tp.Test("A page size past the maximum is refused, not clamped — a clamped page makes a client's arithmetic wrong.", async () =>
    Equal(new[] { "invalid-page-size" }, await ViolationCodes(tp.Responses["PageSizeTooLarge"])));
