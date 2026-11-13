#load "../_shared/Rows.csx"

await tp.Test("tech-north lists exactly the visits of the job assigned to them — both, and nothing else.", async () =>
{
    Equal(2, await PageCount(tp.Responses["TechLists"]));
    Equal(new[] { Constant("Job3401Id") }, (await PageSet(tp.Responses["TechLists"], "work_order_id")).Distinct().ToArray());
});

await tp.Test("spare-north lists nothing, though they ARE the technician on the unassigned job's visit.", async () =>
{
    // The relation decides, not the technician_id column: WO-3402 is assigned to nobody.
    Equal(0, await PageCount(tp.Responses["SpareLists"]));
    Equal(Constant("userSpareNorth"),
        (await BodyOf(tp.Responses["DispatcherReadsThatVisit"])).GetProperty("technician_id").GetString());
});

await tp.Test("The dispatcher lists all three visits (control: the rows exist).", async () =>
{
    Equal(
        new[] { Constant("Job3401Id"), Constant("Job3402Id") }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        (await PageSet(tp.Responses["DispatcherLists"], "work_order_id")).Distinct().ToArray());
    Equal(3, await PageCount(tp.Responses["DispatcherLists"]));
});

await tp.Test("A row failing the relation rule reads 404, and the same id reads 200 for the dispatcher.", async () =>
{
    Equal(404, (int)tp.Responses["TechReadsOthersVisit"].StatusCode);
    Equal(200, (int)tp.Responses["DispatcherReadsThatVisit"].StatusCode);
});

await tp.Test("A booked technician whose job is not assigned to them cannot write the visit — it is 404, and nothing changed.", async () =>
{
    Equal(404, (int)tp.Responses["SpareWritesUnassignedVisit"].StatusCode);
    Equal(System.Text.Json.JsonValueKind.Null,
        (await BodyOf(tp.Responses["DispatcherRereadsThatVisit"])).GetProperty("minutes_on_site").ValueKind);
});

await tp.Test("The assigned technician writes their own visit, but cannot re-point it at a job that is not theirs.", async () =>
{
    Equal(200, (int)tp.Responses["TechWritesOwnVisit"].StatusCode);
    // Refused as an unresolvable reference: a job the caller cannot read is indistinguishable from none.
    Equal(new[] { "unresolved-reference" }, await ViolationCodes(tp.Responses["TechRepointsOwnVisit"]));
    Equal(new[] { "/work_order_id" }, await ViolationPointers(tp.Responses["TechRepointsOwnVisit"]));

    var visit = await BodyOf(tp.Responses["DispatcherReadsTechVisit"]);
    Equal("Valve replaced, pressure tested.", visit.GetProperty("notes").GetString());
    Equal(Constant("Job3401Id"), visit.GetProperty("work_order_id").GetString());
});
