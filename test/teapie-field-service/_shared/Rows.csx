// Shared readers for the field-service suite.
//
// They exist for one reason: every assertion in this suite has to be about the PARSED SHAPE of a
// response — which rows came back, which keys a row carries, which violation codes a refusal names —
// and never about a substring of the whole body. A `Contains("\"items\":[]")` passes on a body that
// also carries a row the caller should not have seen; a set comparison cannot.
//
// Every test script in this suite loads this file on its first line. The directory holds no
// `-req.http`, so TeaPie collects no test case from it.
//
// Do not write the load directive's own spelling into a comment here: TeaPie scans a script for
// directives without skipping comments, so a commented example is resolved as a real one and the
// run dies with "Referenced script ... doesn't exist" naming the rest of the sentence as a path.

using System.Linq;
using System.Net.Http;
using System.Text.Json;

/// One environment constant, as text.
///
/// Not `tp.GetVariable<string>(name)`, and the difference is not cosmetic: TeaPie infers a variable's
/// type from its value, so every GUID-shaped entry in the environment file — every tenant id and every
/// user id this suite compares against — is stored as a `Guid` and `Get<string>` answers **null** for
/// it. An assertion written the obvious way therefore compares `null` with the real value and fails
/// while the product is correct, which is the worst kind of test: red for a reason that has nothing to
/// do with the claim. Reading both shapes here is what keeps that out of every call site.
string Constant(string name) =>
    tp.GetVariable<string>(name)
    ?? (tp.GetVariable<Guid>(name) is var id && id != Guid.Empty ? id.ToString() : null);

/// The response body, parsed. Cloned so it outlives the JsonDocument.
async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
    JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

/// One string field of every row in a page envelope, in the order the page returned them.
async Task<string[]> PageColumn(HttpResponseMessage response, string field)
{
    var body = await BodyOf(response);
    return body.GetProperty("items").EnumerateArray()
        .Select(row => row.GetProperty(field).GetString())
        .ToArray();
}

/// The same, sorted ordinally — for a claim about WHICH rows came back rather than in what order.
async Task<string[]> PageSet(HttpResponseMessage response, string field)
{
    var values = await PageColumn(response, field);
    return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
}

/// How many rows the page carries.
async Task<int> PageCount(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("items").GetArrayLength();
}

/// The page's `next` cursor, or null on the last page. Present-and-null is a contract, not an absence.
async Task<string> NextCursor(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    var next = body.GetProperty("next");
    return next.ValueKind == JsonValueKind.Null ? null : next.GetString();
}

/// Every property name one row object carries.
async Task<string[]> KeysOf(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.EnumerateObject().Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// Every property name every row of a page carries, unioned — so a key present on one row is caught.
async Task<string[]> PageKeys(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("items").EnumerateArray()
        .SelectMany(row => row.EnumerateObject().Select(property => property.Name))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// The `violations` array's machine-readable codes, sorted.
async Task<string[]> ViolationCodes(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("violations").EnumerateArray()
        .Select(violation => violation.GetProperty("code").GetString())
        .OrderBy(code => code, StringComparer.Ordinal).ToArray();
}

/// The `violations` array's JSON pointers, sorted.
async Task<string[]> ViolationPointers(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("violations").EnumerateArray()
        .Select(violation => violation.GetProperty("pointer").GetString())
        .OrderBy(pointer => pointer, StringComparer.Ordinal).ToArray();
}

/// A refusal's problem `type`, asserted as the slug URI rather than looked for in the body text.
async Task<string> ProblemType(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("type").GetString();
}

/// A problem document with everything per-request removed, so two refusals can be compared for
/// being the SAME refusal. `traceId` differs per request by construction and says nothing about
/// which refusal was chosen.
async Task<string> RefusalFingerprint(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    var fields = body.EnumerateObject()
        .Where(property => property.Name != "traceId")
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .Select(property => property.Name + "=" + property.Value.GetRawText());
    return (int)response.StatusCode + "|" + string.Join("|", fields);
}
