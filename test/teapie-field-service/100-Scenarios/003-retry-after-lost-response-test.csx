#load "../_shared/Rows.csx"

await tp.Test("Every retry is answered with the FIRST attempt's result — the same row, the same Location.", async () =>
{
    var first = await BodyOf(tp.Responses["LostAttempt"]);
    var retry = await BodyOf(tp.Responses["Retry"]);
    var again = await BodyOf(tp.Responses["RetryAgain"]);

    Equal(first.GetProperty("id").GetString(), retry.GetProperty("id").GetString());
    Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());

    Equal(
        tp.Responses["LostAttempt"].Headers.Location.ToString(),
        tp.Responses["Retry"].Headers.Location.ToString());
    Equal(
        tp.Responses["LostAttempt"].Headers.Location.ToString(),
        tp.Responses["RetryAgain"].Headers.Location.ToString());

    // Not merely the same id: the same STORED ROW, unmodified. A replay that re-performed the create
    // would move created_at even while returning a matching id.
    Equal(first.GetProperty("created_at").GetString(), again.GetProperty("created_at").GetString());
    Equal(first.GetProperty("updated_at").GetString(), again.GetProperty("updated_at").GetString());
});

await tp.Test("THE STATE OF THE WORLD — three requests, one row. Asserted as a count, which is the only assertion that can tell.", async () =>
{
    var before = await PageColumn(tp.Responses["CountBefore"], "reference");
    var after = await PageColumn(tp.Responses["CountAfter"], "reference");

    Equal(before.Length + 1, after.Length);

    var added = after.Except(before, StringComparer.Ordinal).ToArray();
    Equal(new[] { "WO-8200" }, added);

    // The reference is unique, so a second row could not have been written under it — which is why
    // the count is taken over the customer's whole collection rather than filtered to the reference.
});

await tp.Test("Following the Location the retry returned reaches the row the first attempt created.", async () =>
{
    var first = await BodyOf(tp.Responses["LostAttempt"]);
    var followed = await BodyOf(tp.Responses["FollowLocation"]);

    Equal(first.GetProperty("id").GetString(), followed.GetProperty("id").GetString());
    Equal("Emergency call-out", followed.GetProperty("title").GetString());
    Equal(320.00m, followed.GetProperty("quoted_price").GetDecimal());
    True(followed.GetProperty("is_emergency").GetBoolean());
});
