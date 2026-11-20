#load "../_shared/Rows.csx"

using System.Text.Json;

string[] Roles(JsonElement owner) => owner.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();

await tp.Test("Theo's own company makes him its owner and dispatcher.", async () =>
{
    var garage = await BodyOf(tp.Responses["TheoCreatesGarage"]);

    Equal("Theo's Garage", garage.GetProperty("name").GetString());
    Equal(new[] { "dispatcher" }, Roles(garage));
    True(garage.GetProperty("isOwner").GetBoolean());
});

await tp.Test("A self-made dispatcher cannot write global regions — in his tenant or hers.", async () =>
{
    foreach (var request in new[] { "TheoWritesRegions", "DanaWritesRegions" })
    {
        var refused = await BodyOf(tp.Responses[request]);
        Equal("https://alvo.dev/errors/forbidden", refused.GetProperty("type").GetString());
        Equal("The write was rejected by policy.", refused.GetProperty("detail").GetString());
    }

    // Control: the role the rule names writes the same entity in the same run.
    var region = await BodyOf(tp.Responses["AdminCreatesRegion2"]);
    Equal(tp.GetVariable<string>("RegionCode") + "2", region.GetProperty("code").GetString());
});

await tp.Test("Dana joins the garage as a technician, and stays Acme's dispatcher.", async () =>
{
    var membership = await BodyOf(tp.Responses["DanaAccepts"]);

    Equal("Theo's Garage", membership.GetProperty("name").GetString());
    Equal(new[] { "technician" }, Roles(membership));
    False(membership.GetProperty("isOwner").GetBoolean());
});

await tp.Test("The same person and body: refused as the garage's technician, created as Acme's dispatcher.", async () =>
{
    var refused = await BodyOf(tp.Responses["DanaCreatesInGarage"]);
    Equal("https://alvo.dev/errors/forbidden", refused.GetProperty("type").GetString());

    var created = await BodyOf(tp.Responses["DanaCreatesInAcme"]);
    Equal(Constant("AcmeId"), created.GetProperty("tenant_id").GetString());
    Equal("A garage customer", created.GetProperty("name").GetString());
});
