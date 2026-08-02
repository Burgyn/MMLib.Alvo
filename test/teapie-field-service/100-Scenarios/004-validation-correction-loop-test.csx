#load "../_shared/Rows.csx"

await tp.Test("The refusal names all eight problems at once, each against its own field, with a fix for each.", async () =>
{
    var refused = tp.Responses["Rejected"];

    Equal(
        new[]
        {
            "/contact_email", "/customer_id", "/external_ref", "/priority",
            "/quoted_price", "/reference", "/status", "/title",
        },
        await ViolationPointers(refused));

    Equal(
        new[]
        {
            "enum-value", "format", "format", "max-length",
            "read-only-field", "required", "scale", "unresolved-reference",
        },
        await ViolationCodes(refused));

    foreach (var violation in (await BodyOf(refused)).GetProperty("violations").EnumerateArray())
    {
        NotEmpty(violation.GetProperty("fixSuggestion").GetString());
    }
});

await tp.Test("Fixing EXACTLY what the violations named — no more, no fewer — is sufficient to be accepted.", async () =>
{
    var rejected = System.Text.Json.JsonDocument
        .Parse(await tp.Requests["Rejected"].Content.ReadAsStringAsync()).RootElement;
    var accepted = System.Text.Json.JsonDocument
        .Parse(await tp.Requests["Accepted"].Content.ReadAsStringAsync()).RootElement;

    var before = rejected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);
    var after = accepted.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);

    var changed = before.Keys.Union(after.Keys, StringComparer.Ordinal)
        .Where(key => !before.TryGetValue(key, out var was)
                      || !after.TryGetValue(key, out var now)
                      || !string.Equals(was, now, StringComparison.Ordinal))
        .Select(key => "/" + key)
        .OrderBy(pointer => pointer, StringComparer.Ordinal)
        .ToArray();

    // THE assertion of this scenario. If the two sets differ in either direction the loop is broken:
    // a pointer the refusal named and the correction did not touch means the violation was
    // mis-pointed; a key the correction changed and the refusal never named means the violations
    // were incomplete and the second request only succeeded because something else was silently
    // fixed as well.
    Equal(await ViolationPointers(tp.Responses["Rejected"]), changed);

    Equal(201, (int)tp.Responses["Accepted"].StatusCode);
});

await tp.Test("The corrected create carries the untouched fields exactly as the FIRST attempt sent them.", async () =>
{
    var row = await BodyOf(tp.Responses["ReadTheCorrectedRow"]);

    // These were never named by a violation, were never edited, and are in the stored row — which is
    // what makes "the refusal named everything that was wrong" a claim about the whole payload.
    Equal("Raised by the site manager on Friday.", row.GetProperty("description").GetString());
    Equal(Constant("RegionNorthId"), row.GetProperty("region_id").GetString());
    Equal(Constant("tenantNorth"), row.GetProperty("tenant_id").GetString());

    // And the corrections landed as the fix suggestions prescribed.
    Equal("WO-8300", row.GetProperty("reference").GetString());
    Equal("scheduled", row.GetProperty("status").GetString());
    Equal(2, row.GetProperty("priority").GetInt32());
    Equal(100.00m, row.GetProperty("quoted_price").GetDecimal());
    Equal("site@example.test", row.GetProperty("contact_email").GetString());
    Equal(Constant("CustomerAcmeId"), row.GetProperty("customer_id").GetString());

    // `external_ref` was removed as the fix said to, and the framework left it unset.
    Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("external_ref").ValueKind);
});

await tp.Test("THE STATE OF THE WORLD — the rejected attempt wrote nothing, and the accepted one wrote once.", async () =>
{
    var references = await PageColumn(tp.Responses["RejectedWroteNothing"], "reference");

    Equal(new[] { "WO-8300" }, references);
});
