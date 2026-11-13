#load "../_shared/Rows.csx"

async Task<Dictionary<string, string>> KindsById() =>
    (await BodyOf(tp.Responses["Extensions"])).GetProperty("items").EnumerateArray()
        .ToDictionary(item => item.GetProperty("id").GetString(), item => item.GetProperty("kind").GetString());

await tp.Test("The catalog lists every extension this suite claims the host adds — the command, the hook, both handlers, the schedule and the narrowing.", async () =>
{
    var listed = await KindsById();

    foreach (var id in new[] { "work_orders.dispatch", "within-service-hours", "notify-customer-visit", "escalate-sla-breach", "overdue-report", "technician-open-jobs" })
    {
        True(listed.ContainsKey(id), $"'{id}' is not in the catalog; it lists: {string.Join(", ", listed.Keys)}");
    }
});

