#load "../_shared/Rows.csx"

using System.Text.Json;

// FIRST, and asserting nothing (README, behaviour 1): both secrets, for the removal and reach cases.
await tp.Test("Capture: the billing token and Theo's personal token.", async () =>
{
    async Task<string> Field(string request, string name)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.ToString() : "";
    }

    tp.SetVariable("BillingToken", await Field("DanaMintsBilling", "token"));
    tp.SetVariable("TheoToken", await Field("TheoMintsPersonal", "token"));
});

string[] Texts(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()).ToArray();

await tp.Test("A service token answers its secret once, the header it goes in, and is never cached.", async () =>
{
    var response = tp.Responses["DanaMintsBilling"];
    var billing = await BodyOf(response);

    StartsWith("alvo_svc_", billing.GetProperty("token").GetString());
    Equal("X-Alvo-Api-Key", billing.GetProperty("header").GetString());
    Equal("service", billing.GetProperty("kind").GetString());
    Equal(Constant("AcmeId"), billing.GetProperty("tenantId").GetString());
    Equal(new[] { "dispatcher" }, Texts(billing.GetProperty("roles")));
    Equal(new[] { "work_orders:read" }, Texts(billing.GetProperty("scopes")));
    True(response.Headers.CacheControl?.NoStore ?? false, "a minted secret must answer Cache-Control: no-store");
});

await tp.Test("Billing reads exactly Acme's completed job.", async () =>
{
    Equal(new[] { tp.GetVariable<string>("TheoJobReference") }, await PageSet(tp.Responses["BillingReadsCompleted"], "reference"));
});

await tp.Test("Billing cannot write: its scope is read, whatever its role allows.", async () =>
{
    // The scope's own slug, not the rule's: the refusal names the token's boundary, so an integrator knows to mint a
    // wider token rather than to ask for a role.
    Equal("https://alvo.dev/errors/out-of-scope", await ProblemType(tp.Responses["BillingTriesToWrite"]));

    // Control: the same token, on the same entity, was admitted a request earlier — so the refusal is the verb.
    Equal(200, (int)tp.Responses["BillingReadsCompleted"].StatusCode);
});

await tp.Test("A personal token acts as its person, with no roles of its own.", async () =>
{
    var response = tp.Responses["TheoMintsPersonal"];
    var token = await BodyOf(response);

    StartsWith("alvo_pat_", token.GetProperty("token").GetString());
    Equal("personal", token.GetProperty("kind").GetString());
    Equal(Constant("AcmeId"), token.GetProperty("tenantId").GetString());
    Empty(token.GetProperty("roles").EnumerateArray());
    True(response.Headers.CacheControl?.NoStore ?? false, "a minted secret must answer Cache-Control: no-store");
});

await tp.Test("Theo's token reads exactly his job — his live technician role, narrowed by the row rule.", async () =>
{
    Equal(new[] { tp.GetVariable<string>("TheoJobReference") }, await PageSet(tp.Responses["TheosTokenReads"], "reference"));
});

await tp.Test("Dana's token list names billing, minted by her and active — and no item carries a secret.", async () =>
{
    var tokens = (await BodyOf(tp.Responses["DanaListsTokens"])).EnumerateArray().ToArray();

    // Exactly hers: Theo's personal token is his, and is not on her list.
    var billing = Single(tokens);
    Equal("billing", billing.GetProperty("name").GetString());
    Equal("service", billing.GetProperty("kind").GetString());
    Equal("active", billing.GetProperty("status").GetString());
    Equal(Constant("DanaId"), billing.GetProperty("mintedBy").GetProperty("id").GetString());
    Equal(tp.GetVariable<string>("DanaName"), billing.GetProperty("mintedBy").GetProperty("displayName").GetString());

    foreach (var token in tokens)
    {
        DoesNotContain("token", token.EnumerateObject().Select(member => member.Name));
    }
});
