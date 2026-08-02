#load "../_shared/Rows.csx"

await tp.Test("A duplicate on a `unique` field is a 409 naming the field, like every sibling facet names its own.", async () =>
{
    var refused = tp.Responses["DuplicateUniqueValue"];

    Equal(409, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));

    // The half that makes it repairable, and the half a 500 could not carry: one violation, naming the
    // field, with a stable machine-readable code and a fix suggestion — the same shape 030-Problems/001's
    // 422 carries for every facet the framework CAN check itself.
    Equal(new[] { "unique" }, await ViolationCodes(refused));
    Equal(new[] { "/reference" }, await ViolationPointers(refused));

    var body = await BodyOf(refused);
    var violation = body.GetProperty("violations").EnumerateArray().Single();
    NotNull(violation.GetProperty("fixSuggestion").GetString());
    NotEqual("", violation.GetProperty("fixSuggestion").GetString());
});

await tp.Test("The refusal discloses nothing about the engine — no exception, no SQL, no constraint name.", async () =>
{
    var raw = await tp.Responses["DuplicateUniqueValue"].Content.ReadAsStringAsync();

    DoesNotContain("Npgsql", raw);
    DoesNotContain("PostgresException", raw);
    DoesNotContain("duplicate key", raw);
    DoesNotContain("_bt_check_unique", raw);
    DoesNotContain("23505", raw);
    DoesNotContain("IX_work_orders", raw);
    DoesNotContain("   at ", raw);

    // Nor the value the caller sent. The FIELD name is named — deliberately, and it is the whole point:
    // it is schema-owned, the caller already sent it, and the published document already declares it.
    DoesNotContain("WO-1001", raw);
    Contains("reference", raw);
});

await tp.Test("The identical body with a fresh unique value succeeds, so the 409 was the duplicate and nothing else.", async () =>
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
