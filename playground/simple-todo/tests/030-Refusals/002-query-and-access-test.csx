#load "../../../_shared/Rows.csx"

// Restated rather than inherited: the shared file's own `using` covers the helpers defined there, and a
// TYPE named directly in this script needs the namespace here too.
using System.Text.Json;

// Every refusal in the first half is one of the two 422 kinds, and which one matters: a body the schema
// refuses is `validation`, a query string the parser refuses is `malformed-query`. Same status,
// different fix, so different slug.
async Task MalformedQuery(string request)
{
    Equal(422, (int)tp.Responses[request].StatusCode);
    Equal("https://alvo.dev/errors/malformed-query", await ProblemType(tp.Responses[request]));
}

await tp.Test("An undeclared query key is refused rather than ignored — and the refusal does not confirm it.", async () =>
{
    await MalformedQuery("UnknownKey");

    // `unavailable-field`, not `unknown-field`, and the pointer is `filter` rather than `/colour`.
    // Both are deliberate: "a field you may not read" and "a field that does not exist" have to be
    // ONE answer, or a caller could enumerate an entity's confidential fields by watching which
    // spelling of a refusal came back. The body refusal in 001 names `/colour` freely, because a
    // field you may WRITE is a field whose existence you already know.
    Equal(new[] { "unavailable-field" }, await ViolationCodes(tp.Responses["UnknownKey"]));
    Equal(new[] { "filter" }, await ViolationPointers(tp.Responses["UnknownKey"]));
});

await tp.Test("A misspelled operator is refused rather than falling back to equality.", async () =>
{
    await MalformedQuery("UnknownOperator");

    Equal(new[] { "unknown-operator" }, await ViolationCodes(tp.Responses["UnknownOperator"]));

    // The fix names the whole allow-list, so one round trip is enough to correct the mistake.
    var body = await BodyOf(tp.Responses["UnknownOperator"]);
    var fix = body.GetProperty("violations")[0].GetProperty("fixSuggestion").GetString();
    Contains("eq, neq, gt, gte, lt, lte, like, ilike, in, is", fix);
});

await tp.Test("An operator the field's type does not admit is refused at the parser, not rendered into SQL.", async () =>
{
    await MalformedQuery("OperatorWrongForType");

    Equal(new[] { "unsupported-operator-for-field" }, await ViolationCodes(tp.Responses["OperatorWrongForType"]));

    // And it says which operator and which type, rather than only that something was wrong.
    var body = await BodyOf(tp.Responses["OperatorWrongForType"]);
    var fix = body.GetProperty("violations")[0].GetProperty("fixSuggestion").GetString();
    Contains("'like' cannot be applied to a 'date' field", fix);
});

await tp.Test("A paged read sorted by a nullable field is refused, naming `order` as the thing to change.", async () =>
{
    await MalformedQuery("SortByNullable");

    Equal(new[] { "unpageable-sort-key" }, await ViolationCodes(tp.Responses["SortByNullable"]));
    Equal(new[] { "order" }, await ViolationPointers(tp.Responses["SortByNullable"]));
});

await tp.Test("An unrecognised sort modifier is refused rather than silently ignored.", async () =>
{
    await MalformedQuery("MalformedOrder");

    Equal(new[] { "malformed-order" }, await ViolationCodes(tp.Responses["MalformedOrder"]));
});

await tp.Test("A page size past the maximum is refused, not clamped.", async () =>
{
    await MalformedQuery("PageTooLarge");

    Equal(new[] { "invalid-page-size" }, await ViolationCodes(tp.Responses["PageTooLarge"]));
});

await tp.Test("With every rule `true`, a caller who presents nothing may read, write and delete.", async () =>
{
    // The whole of "no authorization", measured on all four verbs rather than asserted about one. Not
    // a status check either: the write has to have LANDED, because a 200 over an ignored body would
    // look identical.
    var read = await BodyOf(tp.Responses["ReadWithoutCredential"]);
    Equal(RunToken() + "-open", read.GetProperty("title").GetString());

    var written = await BodyOf(tp.Responses["WriteWithoutCredential"]);
    Equal("done", written.GetProperty("status").GetString());

    Equal(204, (int)tp.Responses["DeleteWithoutCredential"].StatusCode);
});

await tp.Test("An audited row written by nobody stamps `created_by` null, not a stand-in identity.", async () =>
{
    var read = await BodyOf(tp.Responses["ReadWithoutCredential"]);

    // The honest answer, and worth pinning: `audit: true` still records WHEN, and records WHO as
    // absent rather than inventing the all-zero uuid — which would be indistinguishable from a real
    // user whose id happened to be zero, and would silently make an ownership rule match every row.
    Equal(JsonValueKind.Null, read.GetProperty("created_by").ValueKind);
    Equal(JsonValueKind.Null, read.GetProperty("updated_by").ValueKind);
    NotEqual(JsonValueKind.Null, read.GetProperty("created_at").ValueKind);
});

await tp.Test("A credential that WAS presented still has to work — 'no auth needed' is not 'any key goes'.", async () =>
{
    // The one thing open rules do not waive, and the one an operator will trip over: a client with a
    // stale key gets 401 here rather than being quietly downgraded to the anonymous caller that would
    // have succeeded. Silently accepting it would hide a misconfiguration for as long as the rules
    // stay open, and break the day somebody tightens them.
    foreach (var request in new[] { "BrokenKeyIsStillRefused", "GarbageKeyIsStillRefused" })
    {
        Equal(401, (int)tp.Responses[request].StatusCode);
        Equal("https://alvo.dev/errors/unauthenticated", await ProblemType(tp.Responses[request]));
    }
});

tp.Test("An undeclared entity and a malformed id are both 404 — the descriptor IS the surface.", () =>
{
    // No route is generated for `projects`, and `/api/todos/not-a-guid` does not match the {id:guid}
    // constraint, so neither reaches a delegate. Asserted together because they share one cause.
    Equal(404, (int)tp.Responses["UndeclaredEntity"].StatusCode);
    Equal(404, (int)tp.Responses["NotAGuid"].StatusCode);
});

await tp.Test("A row nothing holds is 404, and it is a problem document rather than an empty body.", async () =>
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["MissingRow"])));
