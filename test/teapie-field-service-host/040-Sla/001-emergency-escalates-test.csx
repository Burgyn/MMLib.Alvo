#load "../_shared/Rows.csx"

await tp.Test("Capture: the two emergencies the next case reads.", async () =>
{
    tp.SetVariable("Wo7101Id", (await BodyOf(tp.Responses["CreateWo7101"])).GetProperty("id").GetString());
    tp.SetVariable("Wo7102Id", (await BodyOf(tp.Responses["CreateWo7102"])).GetProperty("id").GetString());
});

// ReadWo7101 is the polled request and this file's last, so `tp.Response` is it in every pass.
await tp.Test("WO-7101 escalated to priority 1.", async () =>
{
    var job = await BodyOf(tp.Response);

    Equal(1, job.GetProperty("priority").GetInt32());
    Equal("new", job.GetProperty("status").GetString());
});
