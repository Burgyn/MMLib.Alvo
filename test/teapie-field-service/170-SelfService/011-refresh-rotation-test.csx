#load "../_shared/Rows.csx"

using System.Text.Json;

// FIRST, and asserting nothing (README, behaviour 1): the child and the newest access token, which 012 shows dead.
await tp.Test("Capture: the rotated child and the newest access token.", async () =>
{
    async Task<string> Field(string request, string name)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.ToString() : "";
    }

    tp.SetVariable("DanaChildRefresh", await Field("DanaRefreshes", "refresh_token"));
    tp.SetVariable("DanaLatestAccess", await Field("DanaReplaysInsideWindow", "access_token"));
});

// The polled request is this file's LAST, so `tp.Response` is it in both phases (README, behaviour 4).
await tp.Test("The rotated token, replayed after the grace window, is refused as invalid_grant.", async () =>
{
    Equal(400, (int)tp.Response.StatusCode);
    Equal("invalid_grant", (await BodyOf(tp.Response)).GetProperty("error").GetString());
});

await tp.Test("Control: the newest access token is Dana's session, alive before the reuse.", async () =>
{
    Equal(Constant("DanaId"), (await BodyOf(tp.Responses["LatestAccessBefore"])).GetProperty("id").GetString());
});

await tp.Test("A refresh rotates the refresh token.", async () =>
{
    var child = (await BodyOf(tp.Responses["DanaRefreshes"])).GetProperty("refresh_token").GetString();

    // The values are compared, never printed: a failing Equal would echo two live refresh tokens.
    True(child?.StartsWith("alvo_rt_", StringComparison.Ordinal) == true, "DanaRefreshes' refresh_token starts with alvo_rt_");
    True(!string.Equals(tp.GetVariable<string>("DanaRefresh"), child, StringComparison.Ordinal), "DanaRefreshes' refresh_token differs from DanaRefresh");
});

await tp.Test("A replay inside the window answers the same child, not a second one.", async () =>
{
    var first = (await BodyOf(tp.Responses["DanaRefreshes"])).GetProperty("refresh_token").GetString();
    var replay = (await BodyOf(tp.Responses["DanaReplaysInsideWindow"])).GetProperty("refresh_token").GetString();

    True(string.Equals(first, replay, StringComparison.Ordinal), "DanaReplaysInsideWindow's refresh_token equals DanaRefreshes'");
});
