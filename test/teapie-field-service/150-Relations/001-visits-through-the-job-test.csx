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
