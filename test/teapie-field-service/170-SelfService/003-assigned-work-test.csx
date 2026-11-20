#load "../_shared/Rows.csx"

using System.Text.Json;

// FIRST, and asserting nothing (README, behaviour 1): the job Theo was given and Acme's customer, for later cases — a
// request variable does not outlive its case, so a later file names these, never `DanaCreatesCustomer.response…`.
await tp.Test("Capture: Theo's job and Acme's customer.", async () =>
{
    async Task<string> Id(string request)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var id) ? id.ToString() : "";
    }

    tp.SetVariable("TheoJobId", await Id("DanaCreatesTheosJob"));
    tp.SetVariable("AcmeCustomerId", await Id("DanaCreatesCustomer"));
});

string TheoRef() => tp.GetVariable<string>("TheoJobReference");

string OtherRef() => tp.GetVariable<string>("OtherJobReference");

await tp.Test("A person's create in a tenant lands in that tenant, under her name.", async () =>
{
    var acme = Constant("AcmeId");
    foreach (var request in new[] { "DanaCreatesCustomer", "DanaCreatesTheosJob", "DanaCreatesOtherJob" })
    {
        var row = await BodyOf(tp.Responses[request]);
        Equal(acme, row.GetProperty("tenant_id").GetString());
    }

    // An audited row's stamp is the person who wrote it — her account id, not a key's configured user.
    var job = await BodyOf(tp.Responses["DanaCreatesTheosJob"]);
    Equal(Constant("DanaId"), job.GetProperty("created_by").GetString());
    Equal(Constant("TheoId"), job.GetProperty("assigned_to").GetString());
});

await tp.Test("A scoped create naming another tenant is refused with the fix; leaving tenant_id out is how it works.", async () =>
{
    var refused = tp.Responses["DanaCreatesInAnotherTenant"];
    var body = await BodyOf(refused);

    // The answer the guides quote, whole (#538): it names the field and says to remove it, and repeats no tenant.
    Equal("https://alvo.dev/errors/forbidden", body.GetProperty("type").GetString());
    Equal(
        "Field 'tenant_id' names a tenant other than the one this call acts in, and a row can only be created in the "
        + "caller's own tenant. Remove it from the payload: a created row is placed in the caller's tenant.",
        body.GetProperty("detail").GetString());
    DoesNotContain(Constant("RegionId"), body.GetRawText());

    // Control: the same body WITHOUT tenant_id was a 201 a moment earlier, so the one difference is the refusal's cause.
    Equal(201, (int)tp.Responses["DanaCreatesCustomer"].StatusCode);
});

await tp.Test("In Acme, Theo lists exactly the job assigned to him.", async () =>
{
    Equal(new[] { TheoRef() }, await PageSet(tp.Responses["TheoListsInAcme"], "reference"));
});

await tp.Test("Naming no tenant, Theo is refused by the tenant guard, and sees no row of any tenant.", async () =>
{
    var refused = tp.Responses["TheoListsWithoutTenant"];

    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));
    False((await BodyOf(refused)).TryGetProperty("items", out _), "a refusal carries no page");
});

await tp.Test("A technician cannot place a job: its customer is a row he cannot read.", async () =>
{
    var refused = tp.Responses["TheoCreatesAJob"];

    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
    Equal(new[] { "unresolved-reference" }, await ViolationCodes(refused));
    Equal(new[] { "/customer_id" }, await ViolationPointers(refused));

    // Control: the same customer_id resolved for Dana, who reads customers.
    Equal(201, (int)tp.Responses["DanaCreatesTheosJob"].StatusCode);
});

await tp.Test("A technician holds no create: with nothing to resolve first, the row rule refuses him.", async () =>
{
    var refused = await BodyOf(tp.Responses["TheoCreatesACustomer"]);

    Equal("https://alvo.dev/errors/forbidden", refused.GetProperty("type").GetString());
    Equal("The write was rejected by policy.", refused.GetProperty("detail").GetString());

    // Control: the same body, by a dispatcher of the same tenant, was a 201.
    Equal(201, (int)tp.Responses["DanaCreatesCustomer"].StatusCode);
});

await tp.Test("Theo walks his job through the state machine with his tenant role.", async () =>
{
    var started = await BodyOf(tp.Responses["TheoStarts"]);
    Equal("in_progress", started.GetProperty("status").GetString());

    var completed = await BodyOf(tp.Responses["TheoCompletes"]);
    Equal("completed", completed.GetProperty("status").GetString());
    Equal(tp.GetVariable<string>("Today"), completed.GetProperty("completed_on").GetString());
    Equal(Constant("TheoId"), completed.GetProperty("updated_by").GetString());
});

await tp.Test("Control: Dana lists both jobs in Acme, so Theo's single row is the rule.", async () =>
{
    Equal(
        new[] { TheoRef(), OtherRef() }.OrderBy(reference => reference, StringComparer.Ordinal).ToArray(),
        await PageSet(tp.Responses["DanaListsInAcme"], "reference"));
});
