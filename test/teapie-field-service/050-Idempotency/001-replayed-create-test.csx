#load "../_shared/Rows.csx"

await tp.Test("The replay returns the FIRST create's row — the same id, the same Location, the same values.", async () =>
{
    var first = await BodyOf(tp.Responses["FirstAttempt"]);
    var replay = await BodyOf(tp.Responses["Replay"]);

    Equal(first.GetProperty("id").GetString(), replay.GetProperty("id").GetString());
    Equal(
        tp.Responses["FirstAttempt"].Headers.Location.ToString(),
        tp.Responses["Replay"].Headers.Location.ToString());
    Equal(first.GetProperty("created_at").GetString(), replay.GetProperty("created_at").GetString());
});

await tp.Test("The replay wrote NO second row — asserted as a count, which is the only assertion that can tell.", async () =>
{
    var before = await PageColumn(tp.Responses["CountBefore"], "reference");
    var after = await PageColumn(tp.Responses["CountAfter"], "reference");

    // Two keys were used, so exactly two rows are new. A replay that created a second row would
    // make this three, and every status code in this file would still have been what it should be.
    Equal(before.Length + 2, after.Length);
    Equal(1, await PageCount(tp.Responses["RowsForTheReplayedReference"]));

    var added = after.Except(before, StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToArray();
    Equal(new[] { "WO-6001", "WO-6003" }, added);
});

await tp.Test("The same key with a different body is a 409, and it wrote nothing.", async () =>
{
    var conflict = tp.Responses["SameKeyDifferentBody"];

    Equal("https://alvo.dev/errors/idempotency-conflict", await ProblemType(conflict));
    Null(conflict.Headers.Location);

    var after = await PageColumn(tp.Responses["CountAfter"], "reference");
    DoesNotContain("WO-6002", after);
});

await tp.Test("A DIFFERENT key with the same shape is a genuinely new row, so the key is what scopes the record.", async () =>
{
    var first = await BodyOf(tp.Responses["FirstAttempt"]);
    var other = await BodyOf(tp.Responses["DifferentKeySameShape"]);

    NotEqual(first.GetProperty("id").GetString(), other.GetProperty("id").GetString());
    Equal("WO-6003", other.GetProperty("reference").GetString());
});

await tp.Test("An anonymous caller is told they are unauthorized, not told to fix the header they sent.", async () =>
{
    var refused = tp.Responses["AnonymousKey"];

    // 403 rather than the port's own 422 about the key, because the policy decision is resolved
    // before any header is read. A 401 would be wrong too: no credential was presented and
    // rejected, so there is nothing to challenge.
    Equal(403, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));

    // And nothing about the idempotency header is in the answer, which is the point of the ordering.
    var raw = await refused.Content.ReadAsStringAsync();
    DoesNotContain("Idempotency-Key", raw);

    var after = await PageColumn(tp.Responses["CountAfter"], "reference");
    DoesNotContain("WO-6004", after);
});

await tp.Test("Both rows this case created are removed again, so later groups measure the seeded seven.", async () =>
    Equal(
        new[] { "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007" },
        await PageColumn(tp.Responses["BackToTheSeededSet"], "reference")));
