#load "../../../_shared/Rows.csx"

// Restated rather than inherited: the shared file's own `using` covers the helpers defined there, and
// a TYPE named directly in this script needs the namespace here too.
using System.Text.Json;

await tp.Test("A refused body is an RFC 9457 problem document typed by the KIND of refusal.", async () =>
{
    var refused = tp.Responses["ManyProblems"];

    Equal("application/problem+json", refused.Content.Headers.ContentType.MediaType);

    // Not a type naming the status — that classifies a refusal by a fact the response line already
    // carried, and collapses two refusals whose fixes are different into one.
    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
});

await tp.Test("All three problems are reported at once, each against the field it belongs to.", async () =>
{
    var refused = tp.Responses["ManyProblems"];

    Equal(new[] { "/colour", "/status", "/title" }, await ViolationPointers(refused));
    Equal(new[] { "enum-value", "max-length", "unknown-field" }, await ViolationCodes(refused));
});

await tp.Test("Every violation carries a machine-readable code and a fix an agent can act on.", async () =>
{
    var body = await BodyOf(tp.Responses["ManyProblems"]);

    foreach (var violation in body.GetProperty("violations").EnumerateArray())
    {
        NotEmpty(violation.GetProperty("code").GetString());
        NotEmpty(violation.GetProperty("message").GetString());
        NotEmpty(violation.GetProperty("fixSuggestion").GetString());
    }
});

await tp.Test("No violation echoes the caller's own text back, which is what makes a refusal safe to log.", async () =>
{
    var raw = await tp.Responses["ManyProblems"].Content.ReadAsStringAsync();

    // A message quoting the submitted value puts attacker-controlled bytes into every log that
    // records the response — and answers "what did I just send" for free.
    //
    // The probes are the two VALUES the payload carried. Not "red": the fixSuggestion legitimately
    // ends "…the entity's declared fields", and `declared` contains it — a probe that short is
    // testing English, not the product. The field NAME `colour` is fair game to echo and is echoed,
    // in the pointer, because a field a caller may write is one whose existence they already know.
    DoesNotContain("urgent", raw);
    DoesNotContain(new string('x', 201), raw);

    // And no internal argument name leaked out of a .NET guard.
    DoesNotContain("(Parameter '", raw);
});

await tp.Test("Both required fields are named when neither is sent — not just the first one missing.", async () =>
{
    var refused = tp.Responses["NothingSent"];

    Equal(new[] { "/status", "/title" }, await ViolationPointers(refused));
    Equal(new[] { "required", "required" }, await ViolationCodes(refused));
});

await tp.Test("`description` and `due_on` are genuinely optional, and come back null rather than absent.", async () =>
{
    var created = await BodyOf(tp.Responses["MinimumViable"]);

    Equal(JsonValueKind.Null, created.GetProperty("description").ValueKind);
    Equal(JsonValueKind.Null, created.GetProperty("due_on").ValueKind);
});

await tp.Test("Neither refused write left a row behind — the only row here is the one that was accepted.", async () =>
    Equal(new[] { RunToken() + "-minimal" }, await PageColumn(tp.Responses["NothingWasWritten"], "title")));
