#load "../_shared/Rows.csx"

await tp.Test("Every refresh token of the reused family is dead.", async () =>
{
    Equal("invalid_grant", (await BodyOf(tp.Responses["ChildAfterReuse"])).GetProperty("error").GetString());
});

await tp.Test("The session ended: its newest access token is refused.", async () =>
{
    var refused = tp.Responses["AccessAfterReuse"];
    var challenge = string.Join(", ", refused.Headers.WwwAuthenticate.Select(value => value.ToString()));
    Contains("invalid_token", challenge);
});

await tp.Test("Reuse ended Dana's session only: Theo is still signed in.", async () =>
{
    Equal(Constant("TheoId"), (await BodyOf(tp.Responses["TheoStillSignedIn"])).GetProperty("id").GetString());
});
