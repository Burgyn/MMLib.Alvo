#load "../_shared/Rows.csx"
#load "../_shared/TestIssuer.csx"

// FIRST, and asserting nothing: hands the sessions and ids on to the later cases (README, behaviour 1). Every
// value is read defensively, so a refused sign-in fails the case that names it instead of this capture.
await tp.Test("Capture: the three people's sessions and ids, and the run's region.", async () =>
{
    async Task<string> Field(string request, string name)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == System.Text.Json.JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ToString()
            : "";
    }

    tp.SetVariable("DanaAccess", await Field("DanaSignIn", "access_token"));
    tp.SetVariable("DanaRefresh", await Field("DanaSignIn", "refresh_token"));
    tp.SetVariable("DanaId", await Field("DanaUser", "id"));
    tp.SetVariable("TheoAccess", await Field("TheoSignIn", "access_token"));
    tp.SetVariable("TheoId", await Field("TheoUser", "id"));
    tp.SetVariable("OlgaAccess", await Field("OlgaSignIn", "access_token"));
    tp.SetVariable("OlgaId", await Field("OlgaUser", "id"));
    tp.SetVariable("RegionId", await Field("AdminCreatesRegion", "id"));
});

await tp.Test("The sink publishes the public half of the run's key, under its kid, and nothing private.", async () =>
{
    var keys = (await BodyOf(tp.Responses["IssuerKeys"])).GetProperty("keys").EnumerateArray().ToArray();
    var key = Single(keys);

    Equal(TestIssuerKid(), key.GetProperty("kid").GetString());
    Equal(
        new[] { "alg", "e", "kid", "kty", "n", "use" },
        key.EnumerateObject().Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

    // The members are the RIGHT public half, not merely present: Alvo would refuse every token otherwise, but this
    // says why in one line instead of a 400 three requests later.
    var publicHalf = TestIssuerKey().ExportParameters(false);
    Equal(Base64Url(publicHalf.Modulus), key.GetProperty("n").GetString());
    Equal(Base64Url(publicHalf.Exponent), key.GetProperty("e").GetString());
});

await tp.Test("The metadata names this host's token endpoint and key set.", async () =>
{
    var metadata = await BodyOf(tp.Responses["Metadata"]);
    var issuer = tp.GetVariable<string>("fsBaseUrl") + "/auth/v1";

    // No Alvo:Accounts:Issuer is configured (Development): the issuer is the URL this request reached.
    Equal(issuer, metadata.GetProperty("issuer").GetString());
    Equal(issuer + "/token", metadata.GetProperty("token_endpoint").GetString());
    Equal(issuer + "/.well-known/jwks.json", metadata.GetProperty("jwks_uri").GetString());
    Contains(
        "urn:ietf:params:oauth:grant-type:token-exchange",
        metadata.GetProperty("grant_types_supported").EnumerateArray().Select(value => value.GetString()).ToArray());
});

await tp.Test("A sign-in answers RFC 8693's shape, and is never cached.", async () =>
{
    foreach (var request in new[] { "DanaSignIn", "TheoSignIn", "OlgaSignIn" })
    {
        var response = tp.Responses[request];
        var body = await BodyOf(response);

        Equal("Bearer", body.GetProperty("token_type").GetString());
        Equal(900, body.GetProperty("expires_in").GetInt32());
        Equal("urn:ietf:params:oauth:token-type:access_token", body.GetProperty("issued_token_type").GetString());
        Equal(3, body.GetProperty("access_token").GetString().Split('.').Length);
        // Never StartsWith(prefix, secret): a failing assertion would print the secret (no secret in a printed line).
        True(body.GetProperty("refresh_token").GetString()?.StartsWith("alvo_rt_", StringComparison.Ordinal) == true, $"{request}'s refresh_token starts with alvo_rt_");
        True(response.Headers.CacheControl?.NoStore ?? false, $"{request} must answer Cache-Control: no-store");
    }

    // Three people, three sessions: no two answers share a token.
    var refreshTokens = new List<string>();
    foreach (var request in new[] { "DanaSignIn", "TheoSignIn", "OlgaSignIn" })
    {
        refreshTokens.Add((await BodyOf(tp.Responses[request])).GetProperty("refresh_token").GetString());
    }

    Equal(3, refreshTokens.Distinct(StringComparer.Ordinal).Count());
});

await tp.Test("A token signed by a key the issuer never published is refused.", async () =>
{
    var forged = tp.Responses["ForgedSignIn"];
    Equal(400, (int)forged.StatusCode);
    Equal("invalid_request", (await BodyOf(forged)).GetProperty("error").GetString());
    True(!(await forged.Content.ReadAsStringAsync()).Contains("access_token"), "ForgedSignIn answers no access_token");

    // Control: the same pipeline, the same issuer and the same `kid`, signed by the published key, is a session.
    Equal(200, (int)tp.Responses["OlgaSignIn"].StatusCode);
    Equal(200, (int)tp.Responses["DanaSignIn"].StatusCode);
});

await tp.Test("An ID token whose azp names another client is invalid_request, answered exactly as another client's aud.", async () =>
{
    async Task<string> RefusalWithoutTraceId(string request)
    {
        var response = tp.Responses[request];
        Equal(400, (int)response.StatusCode);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync()).AsObject();
        Equal("invalid_request", body["error"]?.GetValue<string>());
        body.Remove("traceId");
        return body.ToJsonString();
    }

    // One answer for both: a caller learns nothing about which claim failed.
    Equal(await RefusalWithoutTraceId("WrongAudienceSignIn"), await RefusalWithoutTraceId("ForeignAzpSignIn"));
});

await tp.Test("An ID token starts at most one session.", async () =>
{
    var replay = tp.Responses["DanaReplaysIdToken"];
    Equal(400, (int)replay.StatusCode);
    Equal("invalid_request", (await BodyOf(replay)).GetProperty("error").GetString());

    // Control: the very same token was accepted once, moments earlier.
    Equal(200, (int)tp.Responses["DanaSignIn"].StatusCode);
});

await tp.Test("A new person has an account and no membership.", async () =>
{
    var dana = await BodyOf(tp.Responses["DanaUser"]);

    True(Guid.TryParse(dana.GetProperty("id").GetString(), out _));
    Equal(tp.GetVariable<string>("DanaEmail"), dana.GetProperty("email").GetString());
    True(dana.GetProperty("emailTrusted").GetBoolean());
    Equal(tp.GetVariable<string>("DanaName"), dana.GetProperty("name").GetString());
    Equal(new[] { "google" }, dana.GetProperty("providers").EnumerateArray().Select(value => value.GetString()).ToArray());
    Empty(dana.GetProperty("memberships").EnumerateArray());

    // Three people are three accounts, not one account reached three ways.
    var ids = new List<string>();
    foreach (var request in new[] { "DanaUser", "TheoUser", "OlgaUser" })
    {
        ids.Add((await BodyOf(tp.Responses[request])).GetProperty("id").GetString());
    }

    Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
});

await tp.Test("The run's region exists, under the code this run chose.", async () =>
{
    var region = await BodyOf(tp.Responses["AdminCreatesRegion"]);
    Equal(tp.GetVariable<string>("RegionCode"), region.GetProperty("code").GetString());
});
