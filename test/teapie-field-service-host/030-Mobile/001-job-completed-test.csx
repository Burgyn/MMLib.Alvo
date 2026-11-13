#load "../_shared/Rows.csx"

await tp.Test("A forged delivery is 401 and leaves the job scheduled.", async () =>
{
    Equal("scheduled", (await BodyOf(tp.Responses["ReadAfterForged"])).GetProperty("status").GetString());
});

await tp.Test("The signed delivery completed the job: started then completed, with the sign-off day.", async () =>
{
    var job = await BodyOf(tp.Responses["ReadAfterComplete"]);

    Equal("completed", job.GetProperty("status").GetString());
    Equal("2027-03-01", job.GetProperty("completed_on").GetString());
});

await tp.Test("The minutes are on the visit, and the descriptor's rollup carried them to the job.", async () =>
{
    var visit = (await BodyOf(tp.Responses["VisitAfterComplete"])).GetProperty("items")[0];
    var job = await BodyOf(tp.Responses["ReadAfterComplete"]);

    Equal(95, visit.GetProperty("minutes_on_site").GetInt32());
    Equal(95, job.GetProperty("on_site_minutes").GetInt32());
});

await tp.Test("The same delivery again changed nothing: the row's version is the one the first delivery left.", async () =>
{
    Equal(
        (await BodyOf(tp.Responses["ReadAfterComplete"])).GetProperty("updated_at").GetString(),
        (await BodyOf(tp.Responses["ReadAfterReplay"])).GetProperty("updated_at").GetString());
});
