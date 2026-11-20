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

await tp.Test("A refresh rotates the refresh token.", async () =>
{
    var child = (await BodyOf(tp.Responses["DanaRefreshes"])).GetProperty("refresh_token").GetString();

    StartsWith("alvo_rt_", child);
    NotEqual(tp.GetVariable<string>("DanaRefresh"), child);
});

await tp.Test("A replay inside the window answers the same child, not a second one.", async () =>
{
    var first = (await BodyOf(tp.Responses["DanaRefreshes"])).GetProperty("refresh_token").GetString();
    var replay = (await BodyOf(tp.Responses["DanaReplaysInsideWindow"])).GetProperty("refresh_token").GetString();

    Equal(first, replay);
});
