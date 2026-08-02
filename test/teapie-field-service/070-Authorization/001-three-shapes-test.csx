#load "../_shared/Rows.csx"

await tp.Test("SHAPE 1: an operation with no rule is 403, and the refusal names the operation.", async () =>
{
    foreach (var (name, operation) in new[]
    {
        ("DeleteUnconfiguredOperation", "delete"),
        ("AdminAlsoCannotUpdateRegion", "update"),
    })
    {
        Equal(403, (int)tp.Responses[name].StatusCode);
        Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses[name]));

        var detail = (await BodyOf(tp.Responses[name])).GetProperty("detail").GetString();
        Equal($"No policy allows '{operation}' on this entity.", detail);

        // The refusal names neither the entity nor the row, so it answers no existence question.
        DoesNotContain("regions", detail);
    }
});

await tp.Test("The admin meets the same 403, so shape 1 is the missing rule and not a weak caller.", async () =>
{
    // And the same caller reads the same entity happily on a verb the descriptor DOES configure.
    var codes = await PageColumn(tp.Responses["SameCallerCanReadRegions"], "code");
    Equal(new[] { "CENTRAL", "NORTH" }, codes);
});

await tp.Test("SHAPE 2: a caller who fails a configured rule reads 200 with an EMPTY page, never 403.", async () =>
{
    Equal(200, (int)tp.Responses["TechnicianListsCustomers"].StatusCode);
    Equal(0, await PageCount(tp.Responses["TechnicianListsCustomers"]));
    Null(await NextCursor(tp.Responses["TechnicianListsCustomers"]));

    // The control: those rows exist. Without it, the empty page proves nothing about the rule.
    Equal(
        new[] { "Acme Manufacturing", "Borovica Bakery" },
        await PageColumn(tp.Responses["DispatcherListsTheSameCustomers"], "name"));
});

await tp.Test("SHAPE 2 with a ROW-level predicate: the empty page and the subset are the same rule, two callers.", async () =>
{
    // spare-north: a technician with nothing assigned. Same role, same tenant, same scopes as
    // tech-north — so the difference between these two answers is `assigned_to == @user.id` and
    // can be nothing else.
    Equal(0, await PageCount(tp.Responses["SpareTechnicianListsWorkOrders"]));

    // tech-north: assigned exactly three of the seven.
    Equal(
        new[] { "WO-1001", "WO-1002", "WO-1003" },
        await PageColumn(tp.Responses["AssignedTechnicianListsWorkOrders"], "reference"));
});

await tp.Test("SHAPE 3: a row the predicate excludes is 404 — the same 404 a row that never existed answers.", async () =>
{
    Equal(
        await RefusalFingerprint(tp.Responses["NobodyReadsANonexistentRow"]),
        await RefusalFingerprint(tp.Responses["TechnicianReadsRowTheyAreNotAssigned"]));

    Equal("https://alvo.dev/errors/not-found",
        await ProblemType(tp.Responses["TechnicianReadsRowTheyAreNotAssigned"]));
});

await tp.Test("The same technician reads a row they ARE assigned, so shape 3 is the row and not the caller.", async () =>
{
    var row = await BodyOf(tp.Responses["TechnicianReadsTheirOwnRow"]);

    Equal("WO-1001", row.GetProperty("reference").GetString());
    Equal(Constant("userTechNorth"), row.GetProperty("assigned_to").GetString());
});

tp.Test("The three shapes are mutually distinguishable in this one system state.", () =>
{
    var unconfigured = (int)tp.Responses["DeleteUnconfiguredOperation"].StatusCode;
    var callerExcluded = (int)tp.Responses["TechnicianListsCustomers"].StatusCode;
    var rowExcluded = (int)tp.Responses["TechnicianReadsRowTheyAreNotAssigned"].StatusCode;

    Equal(403, unconfigured);
    Equal(200, callerExcluded);
    Equal(404, rowExcluded);

    // Stated as three different numbers on purpose: this is the assertion that goes red if any one
    // of the three behaviours is swapped for another, which is the mutation this group is written
    // to survive.
    Equal(3, new[] { unconfigured, callerExcluded, rowExcluded }.Distinct().Count());
});

await tp.Test("A write is judged row by row too: the assigned row changes and the other one does not.", async () =>
{
    var updated = await BodyOf(tp.Responses["TechnicianUpdatesTheirOwnRow"]);
    Equal("in_progress", updated.GetProperty("status").GetString());

    Equal(404, (int)tp.Responses["TechnicianUpdatesSomebodyElsesRow"].StatusCode);

    // And the refused write really changed nothing — read back by a caller who can see the row.
    var untouched = await BodyOf(tp.Responses["TheRefusedRowIsUnchanged"]);
    Equal("WO-1004", untouched.GetProperty("reference").GetString());
    Equal("in_progress", untouched.GetProperty("status").GetString());
    NotEqual("cancelled", untouched.GetProperty("status").GetString());
});

await tp.Test("A credential that cannot be used is 401 with a challenge — a fourth, distinct diagnosis.", async () =>
{
    var refused = tp.Responses["UnusableCredential"];

    Equal(401, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/unauthenticated", await ProblemType(refused));

    // RFC 7235 §3.1 makes the challenge a MUST, and it is what tells an agent HOW to authenticate.
    NotEmpty(refused.Headers.WwwAuthenticate);
    Contains("X-Alvo-Api-Key", refused.Headers.WwwAuthenticate.ToString());
});
