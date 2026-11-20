#load "../_shared/Rows.csx"

using System.Text.Json;

// FIRST, and asserting nothing (README, behaviour 1): the revoked and the expired code, which 009 presents.
await tp.Test("Capture: the revoked code and the expired code.", async () =>
{
    async Task<string> Code(string request)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var code) ? code.ToString() : "";
    }

    tp.SetVariable("RevokedInviteCode", await Code("RevokedInvite"));
    tp.SetVariable("ExpiredInviteCode", await Code("ExpiringInvite"));
});

// While TeaPie retries, the polled response is not in `tp.Responses` yet (README, behaviour 4): read `tp.Response`.
// ExpiredPreview is this file's LAST request, so `tp.Response` is that response in the final pass too.
await tp.Test("The expiring invitation has expired.", async () =>
{
    Equal(404, (int)tp.Response.StatusCode);
});

await tp.Test("Both invitations were real until they died: created with a code, one revoked by its owner.", async () =>
{
    foreach (var request in new[] { "RevokedInvite", "ExpiringInvite" })
    {
        var invitation = await BodyOf(tp.Responses[request]);
        True(invitation.GetProperty("code").GetString()?.StartsWith("alvo_inv_", StringComparison.Ordinal) == true, $"{request}'s code starts with alvo_inv_");
        Equal("active", invitation.GetProperty("status").GetString());
    }

    Equal(204, (int)tp.Responses["RevokeIt"].StatusCode);
});

await tp.Test("Control: the expiring invitation previewed as alive before it lapsed.", async () =>
{
    Equal("Acme Repairs", (await BodyOf(tp.Responses["StillAlivePreview"])).GetProperty("tenantName").GetString());
});
