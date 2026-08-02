#load "../_shared/Rows.csx"

await tp.Test("Capture: tenant south's ids and the version of its work order.", async () =>
{
    var customer = await BodyOf(tp.Responses["CreateSouthCustomer"]);
    var order = await BodyOf(tp.Responses["CreateSouthWorkOrder"]);

    tp.SetVariable("SouthCustomerId", customer.GetProperty("id").GetString());
    tp.SetVariable("SouthWorkOrderId", order.GetProperty("id").GetString());
    tp.SetVariable("SouthWorkOrderETag", tp.Responses["CreateSouthWorkOrder"].Headers.ETag.ToString());
});

await tp.Test("Tenant south's rows land in tenant south.", async () =>
{
    var customer = await BodyOf(tp.Responses["CreateSouthCustomer"]);
    var order = await BodyOf(tp.Responses["CreateSouthWorkOrder"]);

    Equal(Constant("tenantSouth"), customer.GetProperty("tenant_id").GetString());
    Equal(Constant("tenantSouth"), order.GetProperty("tenant_id").GetString());
    Equal("WO-5001", order.GetProperty("reference").GetString());
});

await tp.Test("A GLOBAL entity is shared: tenant south reads the very region rows tenant north's admin created.", async () =>
{
    var codes = await PageColumn(tp.Responses["SouthReadsGlobalRegions"], "code");

    Equal(new[] { "CENTRAL", "NORTH" }, codes);

    // The same rows, by id — not two rows that merely happen to be spelled the same.
    var ids = await PageColumn(tp.Responses["SouthReadsGlobalRegions"], "id");
    Contains(Constant("RegionNorthId"), ids);
    Contains(Constant("RegionCentralId"), ids);
});
