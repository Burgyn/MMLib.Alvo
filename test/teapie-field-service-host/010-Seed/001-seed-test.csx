#load "../_shared/Rows.csx"

// Capture first, asserting nothing: a SetVariable inside a failing test never runs, and every later case reads these.
await tp.Test("Capture: the region, the customers and the jobs every later case references.", async () =>
{
    tp.SetVariable("RegionNorthId", (await BodyOf(tp.Responses["CreateNorthRegion"])).GetProperty("id").GetString());
    tp.SetVariable("CustomerNorthId", (await BodyOf(tp.Responses["CreateNorthCustomer"])).GetProperty("id").GetString());
    tp.SetVariable("CustomerSouthId", (await BodyOf(tp.Responses["CreateSouthCustomer"])).GetProperty("id").GetString());
    foreach (var reference in new[] { "7001", "7002", "7003", "7004", "7005", "7006" })
    {
        var job = await BodyOf(tp.Responses[$"CreateWo{reference}"]);
        tp.SetVariable($"Wo{reference}Id", job.GetProperty("id").GetString());
        tp.SetVariable($"Wo{reference}JobNumber", job.GetProperty("job_number").GetString());
    }
});

await tp.Test("The customer carries the service hours the host's hook reads.", async () =>
{
    var customer = await BodyOf(tp.Responses["CreateNorthCustomer"]);

    Equal(7, customer.GetProperty("service_opens_hour").GetInt32());
    Equal(19, customer.GetProperty("service_closes_hour").GetInt32());
});

await tp.Test("The descriptor numbers the jobs per tenant, in the embedded host exactly as in the standalone image.", async () =>
{
    var year = (await BodyOf(tp.Responses["CreateWo7001"])).GetProperty("job_number").GetString()!.Substring(4, 4);

    Equal($"JOB-{year}-00001", (await BodyOf(tp.Responses["CreateWo7001"])).GetProperty("job_number").GetString());
    Equal($"JOB-{year}-00005", (await BodyOf(tp.Responses["CreateWo7006"])).GetProperty("job_number").GetString());
    Equal($"JOB-{year}-00001", (await BodyOf(tp.Responses["CreateWo7005"])).GetProperty("job_number").GetString());
});
