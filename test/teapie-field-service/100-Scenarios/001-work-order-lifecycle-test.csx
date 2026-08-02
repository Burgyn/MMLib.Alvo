#load "../_shared/Rows.csx"

await tp.Test("Step 3 — the filtered, ordered list is the three scheduled children, in dispatch order.", async () =>
{
    // Ordered, so this is a sequence rather than a set: the cancelled WO-8004 is excluded by the
    // status filter and the other three come back by ascending priority.
    Equal(
        new[] { "WO-8001", "WO-8002", "WO-8003" },
        await PageColumn(tp.Responses["ListScheduledChildren"], "reference"));

    // And the filter really discriminated: the seeded seven belong to other customers and are not here.
    Equal(3, await PageCount(tp.Responses["ListScheduledChildren"]));
});

await tp.Test("Step 5 — the conditional update applied every field it named and nothing else.", async () =>
{
    var updated = await BodyOf(tp.Responses["UpdateChildB"]);

    Equal("in_progress", updated.GetProperty("status").GetString());
    Equal(Constant("userTechNorth"), updated.GetProperty("assigned_to").GetString());
    Equal(4550.00m, updated.GetProperty("quoted_price").GetDecimal());

    // A PATCH is partial: the fields the body did not mention kept their stored values.
    Equal("Install the unit", updated.GetProperty("title").GetString());
    Equal(2, updated.GetProperty("priority").GetInt32());

    // And the version moved, so a second write with the old tag would now be refused.
    NotEqual(
        tp.Responses["ReadChildB"].Headers.ETag.Tag,
        tp.Responses["UpdateChildB"].Headers.ETag.Tag);
});

await tp.Test("THE STATE OF THE WORLD — the collection is exactly three rows, and each is in its expected state.", async () =>
{
    var final = await BodyOf(tp.Responses["FinalCollection"]);
    var rows = final.GetProperty("items").EnumerateArray().ToArray();

    // Exactly three, in reference order: the deleted one is gone and nothing was created behind the
    // scenes. A count alone would miss a row swapped for another, so each is checked by field below.
    Equal(3, rows.Length);
    Equal(
        new[] { "WO-8001", "WO-8002", "WO-8003" },
        rows.Select(row => row.GetProperty("reference").GetString()).ToArray());

    // The updated one shows the new values.
    var updated = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8002");
    Equal("in_progress", updated.GetProperty("status").GetString());
    Equal(4550.00m, updated.GetProperty("quoted_price").GetDecimal());

    // The untouched ones show the created ones.
    var untouchedA = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8001");
    Equal("scheduled", untouchedA.GetProperty("status").GetString());
    Equal(200.00m, untouchedA.GetProperty("quoted_price").GetDecimal());
    Equal(System.Text.Json.JsonValueKind.Null, untouchedA.GetProperty("assigned_to").ValueKind);

    var untouchedC = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8003");
    Equal("scheduled", untouchedC.GetProperty("status").GetString());
    Equal(600.00m, untouchedC.GetProperty("quoted_price").GetDecimal());
});

await tp.Test("The deleted row is gone by id, and the untouched sibling never moved its version.", async () =>
{
    Equal(404, (int)tp.Responses["DeletedChildIsGone"].StatusCode);
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["DeletedChildIsGone"]));

    // Read back rather than trusted from the write's echo: the echo could be right while the stored
    // row is wrong, and the whole point of a lifecycle scenario is the stored state.
    var updated = await BodyOf(tp.Responses["ReadUpdatedChild"]);
    Equal("in_progress", updated.GetProperty("status").GetString());
    Equal(4550.00m, updated.GetProperty("quoted_price").GetDecimal());

    // The sibling nobody wrote to still carries the version its create minted — so the update in
    // step 5 touched one row and not the collection.
    Equal(
        tp.Responses["CreateChildA"].Headers.ETag.Tag,
        tp.Responses["ReadUntouchedChild"].Headers.ETag.Tag);
});

await tp.Test("Deleting a parent a child still references is a 409 the caller can act on (#138).", async () =>
{
    // The same class 030-Problems/002 pins for `unique`: a constraint the DATABASE enforces, which the
    // framework cannot check before the write. `onDelete: restrict` is the descriptor ASKING for exactly
    // this refusal, so answering 500 — "an invariant Alvo itself relies on is broken" — was the least
    // defensible of the two.
    var refused = tp.Responses["DeleteParentWithChildren"];

    Equal(409, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));

    // One violation, coded `referenced`, carrying the fix. Its pointer is the empty one — RFC 6901's
    // whole document — because a DELETE has no field the caller could change: what has to change is the
    // rows that point at this one.
    Equal(new[] { "referenced" }, await ViolationCodes(refused));
    Equal(new[] { "" }, await ViolationPointers(refused));

    var body = await BodyOf(refused);
    NotNull(body.GetProperty("violations").EnumerateArray().Single()
        .GetProperty("fixSuggestion").GetString());

    // Nothing about the database leaks, and — deliberately — not the referencing entity either: WHICH
    // entity holds the referencing row is a fact about data this caller may have no read access to, so the
    // refusal says only that some record still references this one.
    var raw = await refused.Content.ReadAsStringAsync();
    DoesNotContain("Npgsql", raw);
    DoesNotContain("foreign key", raw);
    DoesNotContain("FOREIGN KEY", raw);
    DoesNotContain("work_orders", raw);
    DoesNotContain("FK_", raw);
    DoesNotContain("   at ", raw);
});
