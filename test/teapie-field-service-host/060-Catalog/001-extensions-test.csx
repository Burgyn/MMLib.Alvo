#load "../_shared/Rows.csx"

async Task<Dictionary<string, string>> KindsById() =>
    (await BodyOf(tp.Responses["Extensions"])).GetProperty("items").EnumerateArray()
        .ToDictionary(item => item.GetProperty("id").GetString(), item => item.GetProperty("kind").GetString());

await tp.Test("The catalog lists every extension this suite claims the host adds — the command, both service-hours hooks, the SLA handler and the schedule.", async () =>
{
    var listed = await KindsById();

    foreach (var id in new[] { "work_orders.dispatch", "within-service-hours", "visit-within-service-hours", "escalate-sla-breach", "overdue-report" })
    {
        True(listed.ContainsKey(id), $"'{id}' is not in the catalog; it lists: {string.Join(", ", listed.Keys)}");
    }
});

