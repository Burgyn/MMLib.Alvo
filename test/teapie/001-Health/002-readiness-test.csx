await tp.Test("Readiness reports the boot phase, and nothing a probe must not read.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();

    Contains("Ready", body);
    DoesNotContain("Password", body);
    DoesNotContain("Host=postgres", body);
});
