#load "../_shared/Rows.csx"

tp.Test("A read of an audited row yields a strong ETag, and every accepted write returns a NEW one.", () =>
{
    var read = tp.Responses["ReadForTag"].Headers.ETag;
    var afterFirstWrite = tp.Responses["WriteWithCurrentTag"].Headers.ETag;
    var afterSecondWrite = tp.Responses["WriteWithRefreshedTag"].Headers.ETag;

    NotNull(read);
    False(read.IsWeak);

    // The tag MOVING is the whole mechanism. A write that returned the tag it was sent would make
    // every later If-Match succeed, and the 412 below would be unreachable — so this assertion is
    // what keeps the stale-tag case from passing for the wrong reason.
    NotEqual(read.Tag, afterFirstWrite.Tag);
    NotEqual(afterFirstWrite.Tag, afterSecondWrite.Tag);
});

await tp.Test("A write carrying the current version succeeds and applies the change.", async () =>
{
    var written = await BodyOf(tp.Responses["WriteWithCurrentTag"]);

    Equal(5, written.GetProperty("priority").GetInt32());
});

await tp.Test("The same tag replayed is stale, and a stale precondition is refused rather than applied.", async () =>
{
    var refused = tp.Responses["WriteWithStaleTag"];

    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));

    var body = await BodyOf(refused);
    Contains("changed since the version this write carries", body.GetProperty("detail").GetString());
});

await tp.Test("The tag the successful write returned is the one the next write must send.", async () =>
{
    var written = await BodyOf(tp.Responses["WriteWithRefreshedTag"]);

    Equal(4, written.GetProperty("priority").GetInt32());
});

await tp.Test("Every precondition this API cannot evaluate is 412 — never ignored, never a 422 about the body.", async () =>
{
    foreach (var name in new[] { "UnknownTag", "WeakTag", "ConditionalCreate" })
    {
        Equal(412, (int)tp.Responses[name].StatusCode);
        Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(tp.Responses[name]));
    }
});

await tp.Test("`If-Match: *` asks only that the row still exist, so it neither refuses nor conditions.", async () =>
{
    var written = await BodyOf(tp.Responses["IfMatchAny"]);

    Equal(4, written.GetProperty("priority").GetInt32());
});

await tp.Test("The end state is the last ACCEPTED write's, so none of the four refusals changed the row.", async () =>
{
    var final = await BodyOf(tp.Responses["FinalState"]);

    // Priority 4 is what IfMatchAny set. The four refused writes asked for 1 and 2 (twice each); if
    // any of them had landed, this would read 1 or 2 rather than 4.
    Equal(4, final.GetProperty("priority").GetInt32());
    Equal("WO-1007", final.GetProperty("reference").GetString());

    // And the refused conditional create wrote nothing.
    Equal("cancelled", final.GetProperty("status").GetString());
});
