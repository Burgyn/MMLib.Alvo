#load "../_shared/Rows.csx"

tp.Test("Both clients really did hold the SAME version — otherwise there was never a conflict to resolve.", () =>
{
    Equal(
        tp.Responses["ClientOneReads"].Headers.ETag.Tag,
        tp.Responses["ClientTwoReads"].Headers.ETag.Tag);
});

await tp.Test("Client 1 wins, and the row moves past the version client 2 is still holding.", async () =>
{
    var written = await BodyOf(tp.Responses["ClientOneWrites"]);
    Equal(1, written.GetProperty("priority").GetInt32());

    NotEqual(
        tp.Responses["ClientTwoReads"].Headers.ETag.Tag,
        tp.Responses["ClientOneWrites"].Headers.ETag.Tag);
});

await tp.Test("Client 2 is refused with 412 — and its own change did NOT land.", async () =>
{
    var refused = tp.Responses["ClientTwoIsRefused"];

    Equal(412, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));

    // The re-read that follows is the evidence: at this point the row still carries client 1's
    // change and none of client 2's, which is what makes the 412 a refusal rather than a warning.
    var afterRefusal = await BodyOf(tp.Responses["ClientTwoRereads"]);
    Equal("scheduled", afterRefusal.GetProperty("status").GetString());
    Equal(1, afterRefusal.GetProperty("priority").GetInt32());
});

tp.Test("The re-read hands client 2 the version client 1's write produced.", () =>
{
    Equal(
        tp.Responses["ClientOneWrites"].Headers.ETag.Tag,
        tp.Responses["ClientTwoRereads"].Headers.ETag.Tag);
});

await tp.Test("THE STATE OF THE WORLD — both writers' changes are present, so neither update was lost.", async () =>
{
    var final = await BodyOf(tp.Responses["FinalContestedRow"]);

    // Client 1's field.
    Equal(1, final.GetProperty("priority").GetInt32());

    // Client 2's field.
    Equal("in_progress", final.GetProperty("status").GetString());

    // This pair is the entire reason If-Match exists. Had client 2's first attempt been served, the
    // row would read priority 3 (client 2's stale copy) and status in_progress — client 1's change
    // silently gone. The assertion that catches that is the priority, not the status.
    Equal("WO-8100", final.GetProperty("reference").GetString());
    Equal(1000.00m, final.GetProperty("quoted_price").GetDecimal());

    // The version moved once per accepted write and not once per attempt.
    NotEqual(
        tp.Responses["ClientTwoRereads"].Headers.ETag.Tag,
        tp.Responses["FinalContestedRow"].Headers.ETag.Tag);
    Equal(
        tp.Responses["ClientTwoRetries"].Headers.ETag.Tag,
        tp.Responses["FinalContestedRow"].Headers.ETag.Tag);
});
