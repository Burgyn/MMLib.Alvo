// Shared readers for every playground suite. One copy for the whole playground rather than one per
// project: a helper that got better for simple-pm should be better for simple-todo too, and two
// copies is how that stops being true.
//
// Every suite's script opens with the load directive naming this file, three levels up from a case
// folder — playground/<project>/tests/<NNN-Group>/<case>-test.csx reaches it as ../../../_shared.
// This directory holds no `-req.http`, and it is outside every suite root anyway, so TeaPie collects
// no test case from it.
//
// They exist for one reason: an assertion has to be about the PARSED SHAPE of a response — which rows
// came back, which keys a row carries, which violation codes a refusal names — and never about a
// substring of the whole body. A `Contains("\"items\":[]")` passes on a body that also carries a row
// the caller should not have seen; a set comparison cannot.
//
// Do not write the load directive's own spelling into a comment here: TeaPie scans a script for
// directives without skipping comments, so a commented example is resolved as a real one and the run
// dies with "Referenced script ... doesn't exist" naming the rest of the sentence as a path.

using System.Linq;
using System.Net.Http;
using System.Text.Json;

/// One environment constant, as text.
///
/// Not `tp.GetVariable<string>(name)`, and the difference is not cosmetic: TeaPie infers a variable's
/// type from its value, so a GUID-shaped entry in the environment file is stored as a `Guid` and
/// `Get<string>` answers **null** for it. An assertion written the obvious way then compares `null`
/// with the real value and fails while the product is correct — red for a reason that has nothing to
/// do with the claim. Reading both shapes here keeps that out of every call site.
string Constant(string name) =>
    tp.GetVariable<string>(name)
    ?? (tp.GetVariable<Guid>(name) is var id && id != Guid.Empty ? id.ToString() : null);

/// This run's token. Every row a suite creates carries it, and every list assertion filters on it, so
/// a second run against the same stack measures its own rows rather than the union with the first's.
string RunToken() => Constant("runToken");

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

/// The page's `next` cursor, or null on the last page. Present-and-null is a contract, not an absence,
/// which is why both members are `required` in the published schema.
async Task<string> NextCursor(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    var next = body.GetProperty("next");
    return next.ValueKind == JsonValueKind.Null ? null : next.GetString();
}

/// Every property name one row object carries, sorted — the shape of a record, as the API publishes it.
async Task<string[]> KeysOf(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.EnumerateObject().Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// Every property name every row of a page carries, unioned — so a key present on only one row is
/// still caught. What `select` narrowed a page to is a claim about this set.
async Task<string[]> PageKeys(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("items").EnumerateArray()
        .SelectMany(row => row.EnumerateObject().Select(property => property.Name))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
}

/// A refusal's problem `type`, asserted as the slug URI rather than looked for in the body text.
async Task<string> ProblemType(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("type").GetString();
}

/// The `violations` array's machine-readable codes, sorted.
async Task<string[]> ViolationCodes(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("violations").EnumerateArray()
        .Select(violation => violation.GetProperty("code").GetString())
        .OrderBy(code => code, StringComparer.Ordinal).ToArray();
}

/// The `violations` array's JSON pointers, sorted — which FIELD a refusal names.
async Task<string[]> ViolationPointers(HttpResponseMessage response)
{
    var body = await BodyOf(response);
    return body.GetProperty("violations").EnumerateArray()
        .Select(violation => violation.GetProperty("pointer").GetString())
        .OrderBy(pointer => pointer, StringComparer.Ordinal).ToArray();
}
