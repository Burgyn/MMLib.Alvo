#load "../_shared/Rows.csx"

// The machine's three answers (state-machines.mdx, "What a refused move answers"): 422 `transition` at
// /status for a move no transition admits or whose `when` fails, 403 `forbidden` for a move a transition
// matches but whose roles the caller lacks, and nothing stored either way.

async Task<string> TransitionMessage(HttpResponseMessage refused) =>
    (await BodyOf(refused)).GetProperty("violations")[0].GetProperty("message").GetString();

await tp.Test("A move no transition admits, or whose `when` fails, is 422 `transition` at /status.", async () =>
{
    foreach (var name in new[]
    {
        "ScheduleWithoutAssignee", "ScheduleWithOnlyAssignee", "ScheduleWithOnlyTime",
        "SkipToCompleted", "CompleteWithoutDate", "AdminReopensKeepingDate",
    })
    {
        var refused = tp.Responses[name];
        Equal(422, (int)refused.StatusCode);
        Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
        Equal(new[] { "transition" }, await ViolationCodes(refused));
        Equal(new[] { "/status" }, await ViolationPointers(refused));
    }
});

await tp.Test("The refusal names neither the stored state nor the one sent.", async () =>
{
    // state-machines.mdx: "The message names neither the stored state nor the one sent." A message
    // naming them would tell a caller the row's state through a write it was refused.
    var message = await TransitionMessage(tp.Responses["SkipToCompleted"]);
    DoesNotContain("scheduled", message);
    DoesNotContain("completed", message);
});

await tp.Test("A transition the caller holds no role for is 403 `forbidden`, not 422.", async () =>
{
    foreach (var name in new[] { "TechnicianCancels", "DispatcherReopens" })
    {
        Equal(403, (int)tp.Responses[name].StatusCode);
        Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses[name]));
    }

    // The control: the same technician moved the same row one request later, by a transition that
    // lists technician — so the 403 is `cancel`'s roles and not the row rule refusing the caller.
    Equal(200, (int)tp.Responses["TechnicianStarts"].StatusCode);
    Equal("in_progress", (await BodyOf(tp.Responses["TechnicianStarts"])).GetProperty("status").GetString());
});

await tp.Test("Each legal move stores its state; a PATCH that does not name status keeps it.", async () =>
{
    Equal("scheduled", (await BodyOf(tp.Responses["ScheduleProperly"])).GetProperty("status").GetString());
    Equal("in_progress", (await BodyOf(tp.Responses["PatchNotNamingStatus"])).GetProperty("status").GetString());
    Equal("completed", (await BodyOf(tp.Responses["CompleteWithDate"])).GetProperty("status").GetString());
    Equal("scheduled", (await BodyOf(tp.Responses["AdminReopens"])).GetProperty("status").GetString());
});

await tp.Test("The job ends reopened by the admin, its sign-off day cleared and its assignee kept.", async () =>
{
    var job = await BodyOf(tp.Responses["ReadBack"]);

    Equal("scheduled", job.GetProperty("status").GetString());
    Equal(System.Text.Json.JsonValueKind.Null, job.GetProperty("completed_on").ValueKind);
    Equal(Constant("userTechNorth"), job.GetProperty("assigned_to").GetString());
    Equal("Lifecycle: radiator leaking, valve replaced", job.GetProperty("title").GetString());
});
