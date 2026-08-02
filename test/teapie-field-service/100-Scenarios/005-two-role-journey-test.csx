#load "../_shared/Rows.csx"

await tp.Test("The baseline: the dispatcher who created all three can see all three.", async () =>
    Equal(
        new[] { "WO-8401", "WO-8402", "WO-8403" },
        await PageColumn(tp.Responses["DispatcherSeesEverything"], "reference")));

await tp.Test("1 — the technician's list is a SUBSET: their two jobs, and neither the third nor the seeded seven.", async () =>
{
    var mine = await PageColumn(tp.Responses["TechnicianListsTheirDay"], "reference");

    // The whole visible set for this caller, unfiltered — so this is "everything I can see", not
    // "everything I asked for". Exactly the two rows assigned to them.
    Equal(new[] { "WO-8401", "WO-8402" }, mine);

    // Non-empty and not everything: both halves matter. An empty page would be shape 2 and a full
    // page would be no rule at all.
    NotEmpty(mine);
    DoesNotContain("WO-8403", mine);
});

await tp.Test("2 — the technician opens and advances a job of their own, under its own version.", async () =>
{
    var opened = await BodyOf(tp.Responses["TechnicianOpensTheirJob"]);
    Equal("WO-8401", opened.GetProperty("reference").GetString());
    Equal(Constant("userSpareNorth"), opened.GetProperty("assigned_to").GetString());

    var advanced = await BodyOf(tp.Responses["TechnicianStartsTheirJob"]);
    Equal("in_progress", advanced.GetProperty("status").GetString());

    // A technician reads a hidden field no more than anyone else does.
    DoesNotContain("access_code", await KeysOf(tp.Responses["TechnicianOpensTheirJob"]));
    DoesNotContain("internal_notes", await KeysOf(tp.Responses["TechnicianOpensTheirJob"]));
});

await tp.Test("3 — an operation no rule covers is 403, and it is NOT the same answer as a rule they fail.", async () =>
{
    Equal(403, (int)tp.Responses["TechnicianDeletesARegion"].StatusCode);
    Equal("No policy allows 'delete' on this entity.",
        (await BodyOf(tp.Responses["TechnicianDeletesARegion"])).GetProperty("detail").GetString());

    // The same caller, in the same instant, meeting a configured rule they fail: 200, empty page.
    Equal(200, (int)tp.Responses["TechnicianListsCustomers"].StatusCode);
    Equal(0, await PageCount(tp.Responses["TechnicianListsCustomers"]));
});

await tp.Test("4 — a job that is not theirs is 404, which is neither of the two answers above.", async () =>
{
    Equal(404, (int)tp.Responses["TechnicianOpensSomebodyElsesJob"].StatusCode);
    Equal(404, (int)tp.Responses["TechnicianWritesSomebodyElsesJob"].StatusCode);
    Equal("https://alvo.dev/errors/not-found",
        await ProblemType(tp.Responses["TechnicianOpensSomebodyElsesJob"]));
});

tp.Test("The four answers this journey walked are four DIFFERENT answers, in one system state.", () =>
{
    var statuses = new[]
    {
        (int)tp.Responses["TechnicianOpensTheirJob"].StatusCode,            // 200, a row
        (int)tp.Responses["TechnicianDeletesARegion"].StatusCode,           // 403, no rule
        (int)tp.Responses["TechnicianOpensSomebodyElsesJob"].StatusCode,    // 404, row excluded
    };

    Equal(new[] { 200, 403, 404 }, statuses);
});

await tp.Test("THE STATE OF THE WORLD — the technician's job advanced and the refused one is untouched.", async () =>
{
    var rows = (await BodyOf(tp.Responses["FinalState"])).GetProperty("items").EnumerateArray().ToArray();

    Equal(3, rows.Length);

    var advanced = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8401");
    Equal("in_progress", advanced.GetProperty("status").GetString());
    Equal(Constant("userSpareNorth"), advanced.GetProperty("updated_by").GetString());

    // The technician's other job was never written, so it is still as the dispatcher created it.
    var untouchedMine = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8402");
    Equal("scheduled", untouchedMine.GetProperty("status").GetString());
    Equal(Constant("userDispatcherNorth"), untouchedMine.GetProperty("updated_by").GetString());

    // And the row the technician was refused twice is exactly as the dispatcher created it — the
    // refused PATCH asked for "cancelled", so a status of "scheduled" is what proves it did not land.
    var refused = rows.Single(row => row.GetProperty("reference").GetString() == "WO-8403");
    Equal("scheduled", refused.GetProperty("status").GetString());
    Equal(Constant("userDispatcherNorth"), refused.GetProperty("updated_by").GetString());
});
