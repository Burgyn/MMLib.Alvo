#load "../_shared/Rows.csx"

async Task Rows(string request, params string[] expected) =>
    Equal(expected, await PageSet(tp.Responses[request], "reference"));

await tp.Test("or=(...) is the union of its terms, and it is wider than either term alone.", async () =>
{
    await Rows("OrGroup", "WO-1005", "WO-1006", "WO-1007");

    // status=eq.cancelled alone is {WO-1007}; status=eq.completed alone is {WO-1005, WO-1006}. The
    // group returning all three is what makes it a union rather than whichever term came last.
    var union = await PageSet(tp.Responses["OrGroup"], "reference");
    Equal(3, union.Length);
});

await tp.Test("and=(...) is the intersection, and it is strictly narrower than its left term alone.", async () =>
{
    await Rows("AndGroup", "WO-1004");
    await Rows("AndLeftTermAlone", "WO-1003", "WO-1004");

    // The control that makes the claim non-vacuous: the left term alone matches two rows, so an
    // `and` that dropped its right term would answer two and this comparison would fail.
    var group = await PageCount(tp.Responses["AndGroup"]);
    var leftAlone = await PageCount(tp.Responses["AndLeftTermAlone"]);
    True(group < leftAlone, "and=(...) did not narrow: it returned as many rows as its left term alone.");
});

await tp.Test("not. negates the term it prefixes, and its answer is the complement of the plain term.", async () =>
    await Rows("NotPrefix", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007"));

await tp.Test("A group and a top-level term conjoin.", async () =>
    // or=(priority.eq.1,priority.eq.4) alone is {1001, 1002, 1007}; conjoining status=scheduled
    // drops the cancelled 1007 and keeps the other two.
    await Rows("GroupAndTerm", "WO-1001", "WO-1002"));

await tp.Test("order returns the rows in the requested order, and the reverse request reverses it.", async () =>
{
    var descending = await PageColumn(tp.Responses["OrderDesc"], "reference");
    var ascending = await PageColumn(tp.Responses["OrderAsc"], "reference");

    Equal(
        new[] { "WO-1007", "WO-1005", "WO-1006", "WO-1003", "WO-1004", "WO-1001", "WO-1002" },
        descending);

    // Not merely `descending.Reverse()` asserted against itself: the second request names the
    // secondary key the other way round too, so this is the sequence the server was asked for.
    Equal(
        new[] { "WO-1002", "WO-1001", "WO-1004", "WO-1003", "WO-1006", "WO-1005", "WO-1007" },
        ascending);
});

await tp.Test("select narrows every row to exactly the named fields, in the order they were named.", async () =>
{
    var keys = await PageKeys(tp.Responses["SelectProjection"]);

    Equal(new[] { "priority", "reference" }, keys);

    // The rows themselves are unchanged — a projection must not also filter.
    await Rows("SelectProjection",
        "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007");

    // And the field order is the request's, which a set comparison cannot see.
    var body = await BodyOf(tp.Responses["SelectProjection"]);
    var first = body.GetProperty("items")[0];
    Equal(
        new[] { "reference", "priority" },
        first.EnumerateObject().Select(property => property.Name).ToArray());
});

await tp.Test("A paged read sorted by a nullable field is answered, and the placement decides the page.", async () =>
{
    // Two of the seven rows carry a `scheduled_for`; five do not. Under `nullslast` the dated pair
    // leads, newest first; under `nullsfirst` the first page is nothing but null-keyed rows. Both
    // requests were a 422 before F4 — every list over HTTP is paged, and a paged read over a nullable
    // sort key was refused, which made half the order grammar unreachable.
    Equal(
        new[] { "WO-1002", "WO-1001" },
        (await PageColumn(tp.Responses["NullableSortKeyLast"], "reference")).Take(2).ToArray());

    var first = await BodyOf(tp.Responses["NullableSortKeyFirst"]);
    True(first.GetProperty("items").EnumerateArray()
        .All(row => row.GetProperty("scheduled_for").ValueKind == System.Text.Json.JsonValueKind.Null));
});

await tp.Test("An unrecognised sort modifier is refused rather than silently ignored.", async () =>
{
    var refused = tp.Responses["MalformedOrder"];

    Equal(new[] { "malformed-order" }, await ViolationCodes(refused));
});
