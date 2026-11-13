#load "../_shared/Rows.csx"

await tp.Test("tech-north lists exactly their open jobs — WO-7001, completed by the app, is not among them.", async () =>
{
    // The descriptor's row rule admits the jobs assigned to tech-north (WO-7001, WO-7002, WO-7006); the host's
    // narrowing then keeps the open ones. A view, not a permission: the rule still decides who may see a row.
    Equal(new[] { "WO-7002", "WO-7006" }, await PageSet(tp.Responses["TechnicianList"], "reference"));
});

await tp.Test("The dispatcher's list still holds WO-7001: the narrowing is the technician's view only.", async () =>
{
    Contains("WO-7001", await PageSet(tp.Responses["DispatcherList"], "reference"));
});
