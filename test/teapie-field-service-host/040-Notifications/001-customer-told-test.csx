#load "../_shared/Rows.csx"

// The sink's journal: `requests[].body` is what the host posted, as a string. While TeaPie retries, only `tp.Response`
// holds the response under evaluation, and CustomerNotices is this file's only request.
async Task<System.Text.Json.JsonElement[]> Notices(string reference) =>
    (await BodyOf(tp.Response)).GetProperty("requests").EnumerateArray()
        .Select(request => System.Text.Json.JsonDocument.Parse(request.GetProperty("body").GetString()).RootElement.Clone())
        .Where(notice => notice.GetProperty("reference").GetString() == reference)
        .ToArray();

await tp.Test("The customer of WO-7001 was told.", async () =>
{
    NotEmpty(await Notices("WO-7001"));
});

await tp.Test("Once, though the dispatch was sent twice — the replay ran nothing — naming the job, its number, the contact and the slot.", async () =>
{
    var notice = Single(await Notices("WO-7001"));

    Equal(Constant("Wo7001Id"), notice.GetProperty("workOrderId").GetString());
    Equal(Constant("Wo7001JobNumber"), notice.GetProperty("jobNumber").GetString());
    Equal("barista@dunaj.test", notice.GetProperty("contactEmail").GetString());
    Equal(DateTimeOffset.Parse("2027-03-01T09:00:00Z"), notice.GetProperty("scheduledFor").GetDateTimeOffset());
});

await tp.Test("The refused dispatches told nobody: their units rolled back, so no `scheduled` move was ever published.", async () =>
{
    Empty(await Notices("WO-7002"));
    Empty(await Notices("WO-7003"));
});
