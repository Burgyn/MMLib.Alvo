#load "../_shared/Rows.csx"

await tp.Test("Dispatch answers the job, the visit it booked and the job's number.", async () =>
{
    var answer = await BodyOf(tp.Responses["DispatchWo7001"]);

    Equal(Constant("Wo7001Id"), answer.GetProperty("workOrder").GetString());
    Equal(Constant("Wo7001JobNumber"), answer.GetProperty("jobNumber").GetString());
    var visits = await BodyOf(tp.Responses["VisitsOfWo7001"]);
    Equal(answer.GetProperty("visit").GetString(), visits.GetProperty("items")[0].GetProperty("id").GetString());
});

await tp.Test("The job is scheduled, assigned and timed — moved by the descriptor's `schedule` transition.", async () =>
{
    var job = await BodyOf(tp.Responses["ReadWo7001"]);

    Equal("scheduled", job.GetProperty("status").GetString());
    Equal(Constant("userTechNorth"), job.GetProperty("assigned_to").GetString());
    Equal(DateTimeOffset.Parse("2027-03-01T09:00:00Z"), job.GetProperty("scheduled_for").GetDateTimeOffset());
});

await tp.Test("Exactly one visit, for tech-north, 09:00 to 10:30 — the retry booked nothing twice.", async () =>
{
    var visits = (await BodyOf(tp.Responses["VisitsOfWo7001"])).GetProperty("items");

    Equal(1, visits.GetArrayLength());
    Equal(Constant("userTechNorth"), visits[0].GetProperty("technician_id").GetString());
    Equal(DateTimeOffset.Parse("2027-03-01T09:00:00Z"), visits[0].GetProperty("starts_at").GetDateTimeOffset());
    Equal(DateTimeOffset.Parse("2027-03-01T10:30:00Z"), visits[0].GetProperty("ends_at").GetDateTimeOffset());
});

await tp.Test("The retry is the first answer replayed byte for byte.", async () =>
{
    Equal(
        await tp.Responses["DispatchWo7001"].Content.ReadAsStringAsync(),
        await tp.Responses["DispatchWo7001Replay"].Content.ReadAsStringAsync());
});

await tp.Test("The technician's refusal is the command's rule: a forbidden problem, though the row is theirs to read.", async () =>
{
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["TechnicianDispatches"]));
});

await tp.Test("An out-of-hours slot is 422 outside-service-hours at /scheduled_for — the host's hook, inside the unit.", async () =>
{
    var refused = tp.Responses["OutsideHours"];

    Equal(new[] { "outside-service-hours" }, await ViolationCodes(refused));
    Equal(new[] { "/scheduled_for" }, await ViolationPointers(refused));
});

await tp.Test("Another tenant's dispatcher finds no such job: 404 not-found, and the rest of this file shows nothing moved.", async () =>
{
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["SouthDispatchesNorthJob"]));
});

await tp.Test("A zero-minute visit is 422 at /durationMinutes, before anything is written.", async () =>
{
    var refused = tp.Responses["DurationZero"];

    Equal(new[] { "out-of-range" }, await ViolationCodes(refused));
    Equal(new[] { "/durationMinutes" }, await ViolationPointers(refused));
});

await tp.Test("A visit that starts in hours but ends after closing is 422 outside-service-hours at /ends_at.", async () =>
{
    var refused = tp.Responses["EndsAfterClosing"];

    Equal(new[] { "outside-service-hours" }, await ViolationCodes(refused));
    Equal(new[] { "/ends_at" }, await ViolationPointers(refused));
});

await tp.Test("Dispatching a job that is already scheduled is 409 conflict.", async () =>
{
    Equal("https://alvo.dev/errors/conflict", await ProblemType(tp.Responses["DispatchAgain"]));
});

await tp.Test("A double-booked technician is the descriptor's exclusion on the calendar.", async () =>
{
    Equal(new[] { "exclusion" }, await ViolationCodes(tp.Responses["DoubleBooking"]));
});

await tp.Test("Every refused dispatch left their job new, unscheduled, unassigned by them, with no visit: all or nothing.", async () =>
{
    var wo7003 = await BodyOf(tp.Responses["ReadWo7003AfterRefusal"]);
    Equal("new", wo7003.GetProperty("status").GetString());
    Equal(System.Text.Json.JsonValueKind.Null, wo7003.GetProperty("scheduled_for").ValueKind);
    Equal(System.Text.Json.JsonValueKind.Null, wo7003.GetProperty("assigned_to").ValueKind);
    Equal(0, await PageCount(tp.Responses["VisitsOfWo7003"]));

    var wo7002 = await BodyOf(tp.Responses["ReadWo7002AfterRefusal"]);
    Equal("new", wo7002.GetProperty("status").GetString());
    Equal(System.Text.Json.JsonValueKind.Null, wo7002.GetProperty("scheduled_for").ValueKind);
    Equal(0, await PageCount(tp.Responses["VisitsOfWo7002"]));
});
