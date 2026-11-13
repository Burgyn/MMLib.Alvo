#load "../_shared/Rows.csx"

async Task<System.Text.Json.JsonElement[]> Reports(string tenant) =>
    (await BodyOf(tp.Response)).GetProperty("requests").EnumerateArray()
        .Select(request => System.Text.Json.JsonDocument.Parse(request.GetProperty("body").GetString()).RootElement.Clone())
        .Where(report => report.GetProperty("tenant").GetString() == tenant)
        .ToArray();

string[] References(System.Text.Json.JsonElement report) =>
    report.GetProperty("references").EnumerateArray().Select(reference => reference.GetString()).ToArray();

await tp.Test("Both tenants received their overdue report.", async () =>
{
    NotEmpty(await Reports(Constant("tenantNorth")));
    NotEmpty(await Reports(Constant("tenantSouth")));
});

await tp.Test("North's report names exactly its own overdue jobs: the stale slot and the breached emergency.", async () =>
{
    // WO-7006 is scheduled for a morning long gone; WO-7101 is new and past its respond_by (050-Sla). Not WO-7001
    // (completed), not WO-7004 (an emergency with no deadline), not WO-7102 (scheduled for 2027) — and never WO-7005,
    // which is south's: each run reads only its own tenant's rows.
    Equal(new[] { "WO-7006", "WO-7101" }, References(Single(await Reports(Constant("tenantNorth")))));
});

await tp.Test("South's report names only south's overdue job.", async () =>
{
    Equal(new[] { "WO-7005" }, References(Single(await Reports(Constant("tenantSouth")))));
});

await tp.Test("The dispatcher's Run now is refused: the Management API's developer level is the admin role's.", async () =>
{
    Equal(403, (int)tp.Responses["DispatcherRunsNow"].StatusCode);
});
