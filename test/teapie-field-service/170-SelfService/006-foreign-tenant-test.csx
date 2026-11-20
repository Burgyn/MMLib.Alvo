#load "../_shared/Rows.csx"

await tp.Test("Naming a tenant she does not belong to refuses Olga outright, as an invalid token.", async () =>
{
    var refused = tp.Responses["OlgaNamesAcme"];

    var challenge = string.Join(", ", refused.Headers.WwwAuthenticate.Select(value => value.ToString()));
    Contains("invalid_token", challenge);
    DoesNotContain("items", await refused.Content.ReadAsStringAsync());
});

await tp.Test("Naming no tenant, Olga is refused by the tenant guard.", async () =>
{
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["OlgaNamesNothing"]));
});

await tp.Test("Control: Acme's dispatcher reads both of Acme's jobs with the same request.", async () =>
{
    Equal(2, await PageCount(tp.Responses["DanaNamesAcme"]));
});

await tp.Test("A tenantless person still reads global data: the run's region.", async () =>
{
    Equal(new[] { tp.GetVariable<string>("RegionCode") }, await PageSet(tp.Responses["OlgaReadsRegions"], "code"));
});
