#load "../_shared/Rows.csx"

const string AppTokenRefusal =
    "Personal and service tokens are app credentials and never manage a project. "
    + "Use an operator session or a configured API key.";

await tp.Test("A service token cannot mint a token.", async () =>
{
    var refused = tp.Responses["BillingMintsAToken"];
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));
    StartsWith("Only a signed-in person manages tenants.", (await BodyOf(refused)).GetProperty("detail").GetString());
    DoesNotContain("alvo_svc_", await refused.Content.ReadAsStringAsync());
});

await tp.Test("Personal and service tokens are refused at Management, with the one fixed sentence.", async () =>
{
    StartsWith("alvo_pat_", (await BodyOf(tp.Responses["DanaMintsPersonal"])).GetProperty("token").GetString());

    foreach (var request in new[] { "PersonalAtManagement", "ServiceAtManagement" })
    {
        var refused = await BodyOf(tp.Responses[request]);
        Equal("https://alvo.dev/errors/forbidden", refused.GetProperty("type").GetString());
        Equal(AppTokenRefusal, refused.GetProperty("detail").GetString());
    }
});

await tp.Test("A Bearer token at Management is an anonymous caller — the same refusal, never a 200.", async () =>
{
    var bearer = tp.Responses["BearerAtManagement"];
    var anonymous = tp.Responses["AnonymousAtManagement"];

    NotEqual(200, (int)bearer.StatusCode);
    Equal((int)anonymous.StatusCode, (int)bearer.StatusCode);
    Equal(await RefusalFingerprint(anonymous), await RefusalFingerprint(bearer));
});

await tp.Test("Control: an operator key reads the descriptor.", async () =>
{
    Equal("field-service", (await BodyOf(tp.Responses["AdminAtManagement"])).GetProperty("project").GetString());
});
