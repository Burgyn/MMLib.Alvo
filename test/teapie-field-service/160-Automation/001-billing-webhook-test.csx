#load "../_shared/Rows.csx"

// What billing received is WireMock's request journal: `requests[].body` is the posted body as a string,
// here the plain CloudEvent of the update (event-envelope.mdx) — no JSONata payload is declared.

// While TeaPie retries, the journal response is not in `tp.Responses` yet — only `tp.Response`, the current
// one, is. BillingJournal is this file's LAST request, so `tp.Response` is that response in both phases.
async Task<string[]> RawBodies() =>
    (await BodyOf(tp.Response)).GetProperty("requests").EnumerateArray()
        .Select(request => request.GetProperty("body").GetString())
        .ToArray();

async Task<System.Text.Json.JsonElement[]> Events(string reference) =>
    (await RawBodies())
        .Select(body => System.Text.Json.JsonDocument.Parse(body).RootElement.Clone())
        .Where(e => e.GetProperty("data").GetProperty("record").GetProperty("reference").GetString() == reference)
        .ToArray();

// FIRST, deliberately: the request's RETRY-UNTIL-TEST-PASS names this test, and TeaPie re-sends the
// journal query until it passes.
await tp.Test("Billing received exactly one completion for WO-3601.", async () =>
{
    var received = await Events("WO-3601");
    Single(received);

    var cloudEvent = received[0];
    Equal("entity.work_orders.updated", cloudEvent.GetProperty("type").GetString());
    Equal("completed", cloudEvent.GetProperty("data").GetProperty("record").GetProperty("status").GetString());
    Equal("in_progress", cloudEvent.GetProperty("data").GetProperty("old_record").GetProperty("status").GetString());
    Equal(Constant("tenantNorth"), cloudEvent.GetProperty("tenantid").GetString());
});

await tp.Test("The delivery names that job — its id and its job number — so billing can invoice it.", async () =>
{
    var record = (await Events("WO-3601"))[0].GetProperty("data").GetProperty("record");
    var created = await BodyOf(tp.Responses["CreateBillable"]);

    Equal(created.GetProperty("id").GetString(), record.GetProperty("id").GetString());
    Equal(created.GetProperty("job_number").GetString(), record.GetProperty("job_number").GetString());
    Equal("PO-3601", record.GetProperty("po_number").GetString());
});

await tp.Test("No delivery carries an excluded field — neither its name nor its value (events.exclude).", async () =>
{
    foreach (var body in await RawBodies())
    {
        DoesNotContain("access_code", body);
        DoesNotContain("SECRET-36", body);
        DoesNotContain("internal_notes", body);
        DoesNotContain("CONFIDENTIAL-36", body);
    }
});

await tp.Test("An update that completes nothing, and a completion without a PO, send billing nothing.", async () =>
{
    Empty(await Events("WO-3602"));
    Empty(await Events("WO-3603"));

    // The controls really happened: both writes were stored, so the silence is the rule's condition.
    Equal(4, (await BodyOf(tp.Responses["TouchControl"])).GetProperty("priority").GetInt32());
    Equal("completed", (await BodyOf(tp.Responses["CompleteNoPo"])).GetProperty("status").GetString());
});
