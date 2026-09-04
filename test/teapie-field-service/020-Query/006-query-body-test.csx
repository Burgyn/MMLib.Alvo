#load "../_shared/Rows.csx"

// The body-shaped collection read (#107), against real PostgreSQL. Every claim here is a COMPARISON
// with the query string that means the same thing — the route's promise is not "it answers" but "it
// answers what the URL answers", and only a real engine can show the transposed parameters reached
// the same statement. The in-process suite proves the parse; this proves the read.

await tp.Test("A body-shaped read answers exactly what the same query string answers.", async () =>
{
    var byBody = await PageColumn(tp.Responses["BodyGroupOrderLimit"], "reference");
    var byUrl = await PageColumn(tp.Responses["UrlGroupOrderLimit"], "reference");

    Equal(byUrl, byBody);

    // Non-empty, so "both surfaces answered nothing" is not what this measured.
    NotEmpty(byBody);
});

await tp.Test("A projection alias survives the body surface and renames the response key.", async () =>
{
    var body = await BodyOf(tp.Responses["BodyAliasedProjection"]);
    var first = body.GetProperty("items").EnumerateArray().First();

    True(first.TryGetProperty("ref", out _), "The alias 'ref' is missing from the projected row.");
    True(first.TryGetProperty("status", out _), "The unaliased 'status' is missing from the projected row.");
    False(
        first.TryGetProperty("reference", out _),
        "The row carries the source name 'reference' as well as its alias, so the projection did not rename.");
});

await tp.Test("An empty object is the empty query: rows, and every field this caller may read.", async () =>
{
    var count = await PageCount(tp.Responses["BodyEmptyQuery"]);
    True(count > 0, "The empty query returned no rows at all.");

    var body = await BodyOf(tp.Responses["BodyEmptyQuery"]);
    var first = body.GetProperty("items").EnumerateArray().First();
    True(first.TryGetProperty("reference", out _), "A row of the unprojected page is missing 'reference'.");
    False(
        first.TryGetProperty("internal_notes", out _),
        "The unprojected page carries a hidden field, so the mask did not reach this surface.");
});

await tp.Test("The tenant boundary holds on this route: a row named by another tenant is not returned.", async () =>
{
    var otherTenant = await PageSet(tp.Responses["BodyOtherTenantByName"], "reference");
    var ownTenant = await PageSet(tp.Responses["BodyOwnTenantByName"], "reference");

    Empty(otherTenant);

    // The control that makes the empty page mean something: the same body, from the row's own
    // tenant, returns the row. Without it, an empty answer would also be satisfied by a filter that
    // matched nothing, a route that returned nothing, or a body the reader silently dropped.
    Equal(new[] { "WO-1001" }, ownTenant);
});

await tp.Test("A masked field and an undeclared one are one refusal on this surface, byte for byte.", async () =>
{
    var masked = await tp.Responses["BodyMaskedField"].Content.ReadAsStringAsync();
    var undeclared = await tp.Responses["BodyUndeclaredField"].Content.ReadAsStringAsync();

    Equal(undeclared, masked);

    // And neither names what was asked about — the whole response, not just the pointer, because a
    // name reaching `detail` or a fix suggestion would be the same leak by another route.
    DoesNotContain("internal_notes", masked);
    DoesNotContain("no_such_field_at_all", undeclared);
});
