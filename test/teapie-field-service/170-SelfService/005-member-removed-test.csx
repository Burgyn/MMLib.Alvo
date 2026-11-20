#load "../_shared/Rows.csx"

using System.Text.Json;

await tp.Test("A removed member's next request in the tenant is refused as an invalid token.", async () =>
{
    var refused = tp.Responses["TheoInAcmeAfter"];

    // RFC 6750 §3: the challenge names the error, so a client knows to drop the tenant, not to retry the request.
    var challenge = string.Join(", ", refused.Headers.WwwAuthenticate.Select(value => value.ToString()));
    Contains("Bearer", challenge);
    Contains("invalid_token", challenge);
    DoesNotContain("items", await refused.Content.ReadAsStringAsync());
});

await tp.Test("His personal token died with his membership.", async () =>
{
    var refused = tp.Responses["TheosTokenAfter"];
    Equal(401, (int)refused.StatusCode);
    DoesNotContain("items", await refused.Content.ReadAsStringAsync());
});

await tp.Test("His session lives on, with no membership.", async () =>
{
    var theo = await BodyOf(tp.Responses["TheoUserAfter"]);

    Equal(Constant("TheoId"), theo.GetProperty("id").GetString());
    Empty(theo.GetProperty("memberships").EnumerateArray());
});

await tp.Test("A service token outlives a member's removal: it is Dana's, and she still owns Acme.", async () =>
{
    Equal(
        new[] { tp.GetVariable<string>("TheoJobReference") },
        await PageSet(tp.Responses["BillingStillReads"], "reference"));
});
