#load "../_shared/Rows.csx"

await tp.Test("KNOWN DEFECT: a duplicate value on a `unique` field is answered 500, where every other declared facet is a 422.", async () =>
{
    var refused = tp.Responses["DuplicateUniqueValue"];

    Equal(500, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/internal", await ProblemType(refused));

    // The correct answer is a 409 or a 422 naming `/reference` with a `unique` code and a fix
    // suggestion, like every sibling facet. When that lands, this test goes red and this file is
    // the place to update — which is the whole reason it asserts the defect rather than skipping it.
    var body = await BodyOf(refused);
    False(body.TryGetProperty("violations", out _),
        "The 500 now carries violations, so the unique constraint is being validated — replace this "
        + "case with the 409/422 assertion it was written to be replaced by.");
});

await tp.Test("The 500 discloses nothing about the failure — no exception, no SQL, no constraint name.", async () =>
{
    var raw = await tp.Responses["DuplicateUniqueValue"].Content.ReadAsStringAsync();

    DoesNotContain("Npgsql", raw);
    DoesNotContain("PostgresException", raw);
    DoesNotContain("duplicate key", raw);
    DoesNotContain("_bt_check_unique", raw);
    DoesNotContain("   at ", raw);

    // Nor the value the caller sent, nor the column it collided on.
    DoesNotContain("WO-1001", raw);
    DoesNotContain("reference", raw);
});

await tp.Test("The identical body with a fresh unique value succeeds, so the 500 was the duplicate and nothing else.", async () =>
{
    var created = await BodyOf(tp.Responses["SameBodyFreshReference"]);

    Equal("WO-3900", created.GetProperty("reference").GetString());
    Equal("Duplicate reference", created.GetProperty("title").GetString());
});

await tp.Test("The refused create wrote no second row: the unique value still names exactly one row.", async () =>
    Equal(1, await PageCount(tp.Responses["AfterTheDuplicate"])));

await tp.Test("The comparison row is gone again, so the world every later group measures is the seeded seven.", async () =>
    Equal(
        new[] { "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007" },
        await PageColumn(tp.Responses["BackToTheSeededSet"], "reference")));
