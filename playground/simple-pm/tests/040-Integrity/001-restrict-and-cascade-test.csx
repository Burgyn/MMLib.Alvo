#load "../../../_shared/Rows.csx"

async Task Rows(string request, params string[] tails) =>
    Equal(tails.Select(tail => RunToken() + " " + tail).ToArray(),
          await PageColumn(tp.Responses[request], "title"));

await tp.Test("Deleting a person who still holds work is 409 `conflict`, and it names the reason.", async () =>
{
    var refused = tp.Responses["DeleteAdaRefused"];

    // The second of the two facets nothing can check before the write — like a `unique` collision,
    // only the engine knows whether a row is still referenced. 409 and not 500: a caller deleting a
    // person who has open tasks has not broken an invariant Alvo relies on.
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));
    Equal(new[] { "referenced" }, await ViolationCodes(refused));
});

tp.Test("A person nothing references is deleted — so the refusal above is the reference, not the verb.", () =>
    Equal(204, (int)tp.Responses["DeleteZoe"].StatusCode));

await tp.Test("`cascade` takes the deleted milestone's tasks, and only those.", async () =>
{
    Equal(204, (int)tp.Responses["DeleteBeta"].StatusCode);

    // Beta held `a` and `c`; both are gone. `d` was on Launch and `e` on no milestone, and both
    // remain — which is what makes this a cascade rather than a delete of everything. `b` left Beta
    // in 002 by an explicit null, so it survives too, and that is the same claim from the other side.
    await Rows("TasksAfterCascade",
        "b write the endpoints", "d write the announcement", "e someone should look at this");
});

await tp.Test("With the referencing task gone, the same delete succeeds — the restrict lifted with it.", async () =>
{
    Equal(204, (int)tp.Responses["DeleteAdaNow"].StatusCode);

    // The full arc in one assertion: identical request, refused then accepted, and the only thing
    // that changed in between is whether a row pointed at her.
    Equal(new[] { RunToken() + " Grace Hopper" }, await PageColumn(tp.Responses["TeamAfter"], "name"));
});
