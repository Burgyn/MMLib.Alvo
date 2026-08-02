#load "../_shared/Rows.csx"

await tp.Test("The refusal is an RFC 9457 problem document typed by the KIND of refusal, not by its status.", async () =>
{
    var refused = tp.Responses["ManyViolations"];

    Equal("application/problem+json", refused.Content.Headers.ContentType.MediaType);

    // Not "https://tools.ietf.org/html/rfc9110#section-15.5.21", which is what the framework's own
    // Results.Problem would have stamped: a type naming the status classifies a refusal by a fact
    // the response line already carried, and two refusals with different fixes become one.
    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));

    var body = await BodyOf(refused);
    Equal(422, body.GetProperty("status").GetInt32());
});

await tp.Test("Every one of the nine problems is reported, each against the field it belongs to.", async () =>
{
    var refused = tp.Responses["ManyViolations"];

    Equal(
        new[]
        {
            "/contact_email", "/customer_id", "/external_ref", "/priority",
            "/quoted_price", "/reference", "/region_id", "/status", "/title",
        },
        await ViolationPointers(refused));

    Equal(
        new[]
        {
            "enum-value", "format", "format", "max-length", "read-only-field",
            "required", "scale", "unresolved-reference", "unresolved-reference",
        },
        await ViolationCodes(refused));
});

await tp.Test("Each violation carries a machine-readable code and a fix suggestion, which is the contract.", async () =>
{
    var body = await BodyOf(tp.Responses["ManyViolations"]);

    foreach (var violation in body.GetProperty("violations").EnumerateArray())
    {
        NotEmpty(violation.GetProperty("code").GetString());
        NotEmpty(violation.GetProperty("message").GetString());
        NotEmpty(violation.GetProperty("fixSuggestion").GetString());
    }
});

await tp.Test("No violation echoes the caller's own text back, which is what makes the refusal safe to log.", async () =>
{
    var raw = await tp.Responses["ManyViolations"].Content.ReadAsStringAsync();

    // The values the payload sent. A message quoting one of them would put attacker-controlled
    // bytes into every log that records the response, and would answer "what did I send" for free.
    DoesNotContain("NOT-A-REFERENCE", raw);
    DoesNotContain("not-an-email", raw);
    DoesNotContain("mine-to-set", raw);
    DoesNotContain("pending", raw);

    // And no internal argument name leaked out of a .NET guard.
    DoesNotContain("(Parameter '", raw);
});

await tp.Test("An undeclared key is refused rather than ignored, and named by its own pointer.", async () =>
{
    var refused = tp.Responses["UnknownField"];

    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
    Equal(new[] { "unknown-field" }, await ViolationCodes(refused));
    Equal(new[] { "/urgency" }, await ViolationPointers(refused));
});

await tp.Test("A malformed query is a different slug from a refused body — different fix, different type.", async () =>
{
    Equal("https://alvo.dev/errors/malformed-query", await ProblemType(tp.Responses["MalformedQueryKind"]));
    Equal(new[] { "invalid-page-size" }, await ViolationCodes(tp.Responses["MalformedQueryKind"]));
});

await tp.Test("None of the refused writes left a row behind: the collection is still the seeded seven.", async () =>
{
    var references = await PageColumn(tp.Responses["NothingWasWritten"], "reference");

    Equal(
        new[] { "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007" },
        references);
});
