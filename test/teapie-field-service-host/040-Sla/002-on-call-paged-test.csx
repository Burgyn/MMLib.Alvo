#load "../_shared/Rows.csx"

async Task<System.Text.Json.JsonElement[]> Pages(string reference) =>
    (await BodyOf(tp.Response)).GetProperty("requests").EnumerateArray()
        .Select(request => System.Text.Json.JsonDocument.Parse(request.GetProperty("body").GetString()).RootElement.Clone())
        .Where(page => page.GetProperty("reference").GetString() == reference)
        .ToArray();

await tp.Test("On-call was paged about WO-7101.", async () =>
{
    NotEmpty(await Pages("WO-7101"));
});

await tp.Test("One page, naming the job and the deadline it missed.", async () =>
{
    var page = Single(await Pages("WO-7101"));

    Equal(Constant("Wo7101Id"), page.GetProperty("workOrderId").GetString());
    Equal(DateTimeOffset.Parse(tp.GetVariable<string>("RespondBy")), page.GetProperty("respondBy").GetDateTimeOffset());
});

await tp.Test("The control was scheduled before its deadline: still priority 3, and nobody was paged about it.", async () =>
{
    var control = await BodyOf(tp.Responses["ReadWo7102"]);

    Equal(3, control.GetProperty("priority").GetInt32());
    Equal("scheduled", control.GetProperty("status").GetString());
    Empty(await Pages("WO-7102"));
});

// The `when` excludes an emergency WITHOUT a deadline: WO-7004 was never armed, so nothing pages for it either.
await tp.Test("An emergency with no respond_by arms no timer and pages nobody.", async () =>
{
    Empty(await Pages("WO-7004"));
});
