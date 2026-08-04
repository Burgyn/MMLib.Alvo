#load "../../../_shared/Rows.csx"

// The five titles this run created, as the reads should report them. Built from the token rather than
// written out, so the expectations and the requests cannot disagree about which run's rows they mean.
string[] Titles(params string[] letters) =>
    letters.Select(letter => RunToken() + "-page " + letter).ToArray();

async Task Rows(string request, params string[] letters) =>
    Equal(Titles(letters), await PageColumn(tp.Responses[request], "title"));

await tp.Test("The run's five rows come back in the requested order — and the reverse request reverses it.", async () =>
{
    await Rows("All", "a", "b", "c", "d", "e");

    // Not `All.Reverse()` compared with itself: this is the sequence the server was asked for, and an
    // `order` that was parsed and then ignored would answer both requests identically.
    await Rows("AllDescending", "e", "d", "c", "b", "a");
});

await tp.Test("An equality term narrows to the rows that hold the value, and nothing else.", async () =>
    await Rows("Done", "d", "e"));

await tp.Test("`in.(...)` is membership, and it answers the same set as the `or` group that spells it out.", async () =>
{
    await Rows("NotFinished", "a", "b", "c");

    // The two spellings agreeing is the claim. `in.(todo,doing)` and `or=(status.eq.todo,...)` are
    // the same question, and a build where one of them silently matched nothing would still answer
    // 200 with an empty page — which only a comparison like this catches.
    var membership = await PageSet(tp.Responses["NotFinished"], "title");
    var negation = await PageSet(tp.Responses["NotDone"], "title");
    Equal(membership, negation);
});

await tp.Test("A group unions its terms, and conjoins with the top-level term beside it.", async () =>
    // todo ∪ done is {a,b,d,e}; `c` is `doing` and is the row that proves the union is not "everything".
    await Rows("TodoOrDone", "a", "b", "d", "e"));

await tp.Test("A range over a nullable field excludes the nulls — SQL's three-valued logic, not a bug.", async () =>
{
    // a (09-01) and c (09-03) are in range; e (09-05) is out; b and d carry no date at all.
    await Rows("DueEarly", "a", "c");

    // And `is.null` is how you ask for the two the range cannot see. The two sets being disjoint and
    // covering all five is what says the range dropped them for being NULL rather than for a value.
    await Rows("NoDueDate", "b", "d");
});

await tp.Test("`select` narrows the row to the named fields, in the order named — and does not filter.", async () =>
{
    Equal(new[] { "status", "title" }, await PageKeys(tp.Responses["Projection"]));

    // The set is untouched: a projection that also filtered would be a different query.
    await Rows("Projection", "a", "b", "c", "d", "e");

    // Field order is the request's, which a sorted set comparison cannot see.
    var body = await BodyOf(tp.Responses["Projection"]);
    Equal(
        new[] { "title", "status" },
        body.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).ToArray());
});
