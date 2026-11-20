#load "../_shared/Rows.csx"

await tp.Test("Used up, revoked, expired and unknown codes are one refusal.", async () =>
{
    var fingerprints = new List<string>();
    foreach (var request in new[] { "UsedUp", "Revoked", "Expired", "Unknown" })
    {
        fingerprints.Add(await RefusalFingerprint(tp.Responses[request]));
    }

    Single(fingerprints.Distinct(StringComparer.Ordinal));
    StartsWith("404|", fingerprints[0]);
});

await tp.Test("No dead code made Olga a member of anything.", async () =>
{
    var olga = await BodyOf(tp.Responses["OlgaUserAfter"]);

    Equal(Constant("OlgaId"), olga.GetProperty("id").GetString());
    Empty(olga.GetProperty("memberships").EnumerateArray());
});
