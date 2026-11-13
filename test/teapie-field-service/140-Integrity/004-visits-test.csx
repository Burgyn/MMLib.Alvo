#load "../_shared/Rows.csx"

await tp.Test("Capture: the job and visit ids 150-Relations reads.", async () =>
{
    tp.SetVariable("Job3401Id", (await BodyOf(tp.Responses["CreateJob3401"])).GetProperty("id").GetString());
    tp.SetVariable("Job3402Id", (await BodyOf(tp.Responses["CreateJob3402"])).GetProperty("id").GetString());
    tp.SetVariable("VisitOfTechNorthId", (await BodyOf(tp.Responses["V1"])).GetProperty("id").GetString());
    tp.SetVariable("VisitOnUnassignedJobId",
        (await BodyOf(tp.Responses["OtherTechSameSlot"])).GetProperty("id").GetString());
});

await tp.Test("An overlapping visit for one technician is 409 `exclusion` at /starts_at, naming nothing of the other.", async () =>
{
    var refused = tp.Responses["Overlap"];
    Equal(409, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));
    Equal(new[] { "exclusion" }, await ViolationCodes(refused));
    Equal(new[] { "/starts_at" }, await ViolationPointers(refused));

    // The record it collided with is not the caller's to learn: neither its id nor its range.
    var raw = await refused.Content.ReadAsStringAsync();
    DoesNotContain((await BodyOf(tp.Responses["V1"])).GetProperty("id").GetString(), raw);
    DoesNotContain("10:00", raw);
    DoesNotContain("11:00", raw);
});

await tp.Test("Back-to-back slots and another technician's identical slot are both stored.", async () =>
{
    Equal(201, (int)tp.Responses["BackToBack"].StatusCode);
    Equal(201, (int)tp.Responses["OtherTechSameSlot"].StatusCode);
    Equal(Constant("userSpareNorth"),
        (await BodyOf(tp.Responses["OtherTechSameSlot"])).GetProperty("technician_id").GetString());
});

await tp.Test("A visit on a completed or a cancelled job is 422 `check` at /work_order_id, with the descriptor's message.", async () =>
{
    foreach (var name in new[] { "OnClosedJob", "OnCancelledJob" })
    {
        var refused = tp.Responses[name];
        Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
        Equal(new[] { "check" }, await ViolationCodes(refused));
        Equal(new[] { "/work_order_id" }, await ViolationPointers(refused));
        Equal("A visit can only be booked on an open work order.",
            (await BodyOf(refused)).GetProperty("violations")[0].GetProperty("message").GetString());
    }
});

await tp.Test("The job's on_site_minutes is the sum of its visits' minutes (45 + 30), kept by the writes.", async () =>
{
    Equal(75, (await BodyOf(tp.Responses["ReadJob3401"])).GetProperty("on_site_minutes").GetInt32());
});
