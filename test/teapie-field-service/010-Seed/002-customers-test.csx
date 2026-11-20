#load "../_shared/Rows.csx"

await tp.Test("Capture: the two customer ids every later case references.", async () =>
{
    var acme = await BodyOf(tp.Responses["CreateAcme"]);
    var borovica = await BodyOf(tp.Responses["CreateBorovica"]);

    tp.SetVariable("CustomerAcmeId", acme.GetProperty("id").GetString());
    tp.SetVariable("CustomerBorovicaId", borovica.GetProperty("id").GetString());
});

await tp.Test("A created customer lands in the caller's own tenant and echoes it back.", async () =>
{
    var acme = await BodyOf(tp.Responses["CreateAcme"]);
    var borovica = await BodyOf(tp.Responses["CreateBorovica"]);

    Equal(Constant("tenantNorth"), acme.GetProperty("tenant_id").GetString());
    Equal("Acme Manufacturing", acme.GetProperty("name").GetString());
    Equal("priority", acme.GetProperty("tier").GetString());
    Equal("standard", borovica.GetProperty("tier").GetString());
});

tp.Test("`customers` is not audited, so no row of it is ever handed an ETag.", () =>
{
    Null(tp.Responses["CreateAcme"].Headers.ETag);
    Null(tp.Responses["CreateBorovica"].Headers.ETag);
});

await tp.Test("Both tenancy refusals name tenant_id and the fix, repeat no tenant, and neither wrote a row.", async () =>
{
    const string ForeignTenant = "Field 'tenant_id' names a tenant other than the one this call acts in, and a row can "
        + "only be created in the caller's own tenant. Remove it from the payload: a created row is placed in the "
        + "caller's tenant.";

    foreach (var request in new[] { "CreateWithNullTenant", "CreateIntoAnotherTenant" })
    {
        var refused = await BodyOf(tp.Responses[request]);
        Equal("https://alvo.dev/errors/forbidden", refused.GetProperty("type").GetString());
        Equal(ForeignTenant, refused.GetProperty("detail").GetString());
        Null(tp.Responses[request].Headers.Location);
    }

    DoesNotContain(Constant("tenantSouth"), (await BodyOf(tp.Responses["CreateIntoAnotherTenant"])).GetRawText());
});

await tp.Test("The state after the two refusals is exactly the two rows that were accepted.", async () =>
{
    var names = await PageSet(tp.Responses["ListNorthCustomers"], "name");

    Equal(new[] { "Acme Manufacturing", "Borovica Bakery" }, names);
});
