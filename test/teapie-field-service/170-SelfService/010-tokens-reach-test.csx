#load "../_shared/Rows.csx"

const string AppTokenRefusal =
    "Personal and service tokens are app credentials and never manage a project. "
    + "Use an operator session or a configured API key.";

await tp.Test("A service token cannot mint a token.", async () =>
{
    var refused = tp.Responses["BillingMintsAToken"];
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));
    StartsWith("Only a signed-in person manages tenants.", (await BodyOf(refused)).GetProperty("detail").GetString());
    True(!(await refused.Content.ReadAsStringAsync()).Contains("alvo_svc_"), "BillingMintsAToken echoes no service secret");
});

await tp.Test("An app token sent as Authorization: Bearer is refused as an invalid token, with no rows.", async () =>
{
    var refused = tp.Responses["BillingAsBearer"];

    // The answer the guides quote: the generic refusal of an unusable bearer token — it names neither the header
    // the secret belongs in nor what kind of credential it is.
    Equal("https://alvo.dev/errors/unauthenticated", await ProblemType(refused));
    Equal(
        "The presented bearer token could not be used. Sign in again, or check that it has not expired or been revoked "
            + "and that you belong to the tenant you requested.",
        (await BodyOf(refused)).GetProperty("detail").GetString());
    var challenge = string.Join(", ", refused.Headers.WwwAuthenticate.Select(value => value.ToString()));
    Contains("invalid_token", challenge);
    False((await BodyOf(refused)).TryGetProperty("items", out _), "a refusal carries no page");

    // Control: the same secret, in X-Alvo-Api-Key, reads both of Acme's jobs — it acts as a dispatcher there.
    Equal(
        new[] { tp.GetVariable<string>("TheoJobReference"), tp.GetVariable<string>("OtherJobReference") }
            .OrderBy(reference => reference, StringComparer.Ordinal).ToArray(),
        await PageSet(tp.Responses["BillingAsKey"], "reference"));
});

await tp.Test("Personal and service tokens are refused at Management, with the one fixed sentence.", async () =>
{
    True((await BodyOf(tp.Responses["DanaMintsPersonal"])).GetProperty("token").GetString()?.StartsWith("alvo_pat_", StringComparison.Ordinal) == true, "DanaMintsPersonal's token starts with alvo_pat_");

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
