#load "../_shared/Rows.csx"

await tp.Test("Two callers with identical rights read two disjoint sets of the same entity.", async () =>
{
    var south = await PageSet(tp.Responses["SouthListsWorkOrders"], "reference");
    var north = await PageSet(tp.Responses["NorthListsWorkOrders"], "reference");

    Equal(new[] { "WO-5001" }, south);
    Equal(new[] { "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007" }, north);

    // Disjoint, asserted rather than eyeballed: a scope that leaked one row would show here even if
    // the two lists above were both non-empty and both plausible.
    Empty(south.Intersect(north, StringComparer.Ordinal));

    // Both non-empty, so "the tenant sees nothing at all" is not what this measured.
    NotEmpty(south);
    NotEmpty(north);
});

await tp.Test("A scoped list carries only the caller's own tenant, on every entity.", async () =>
{
    var names = await PageColumn(tp.Responses["SouthListsCustomers"], "name");
    Equal(new[] { "Southern Foods" }, names);

    var body = await BodyOf(tp.Responses["SouthListsWorkOrders"]);
    foreach (var row in body.GetProperty("items").EnumerateArray())
    {
        Equal(Constant("tenantSouth"), row.GetProperty("tenant_id").GetString());
    }
});

await tp.Test("A filter naming the other tenant's row returns nothing — the scope narrows, it does not refuse.", async () =>
{
    Equal(200, (int)tp.Responses["SouthFiltersForNorthsReference"].StatusCode);
    Equal(0, await PageCount(tp.Responses["SouthFiltersForNorthsReference"]));
});

await tp.Test("Reading, updating or deleting the other tenant's row is 404 — never a 403 that would confirm it.", async () =>
{
    foreach (var name in new[] { "SouthReadsNorthsRow", "SouthUpdatesNorthsRow", "SouthDeletesNorthsRow" })
    {
        Equal(404, (int)tp.Responses[name].StatusCode);
        Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses[name]));
    }

    // Indistinguishable from an id that exists in no tenant at all: the whole document matches.
    Equal(
        await RefusalFingerprint(tp.Responses["SouthReadsANonexistentRow"]),
        await RefusalFingerprint(tp.Responses["SouthReadsNorthsRow"]));

    // And the control: the same id, in the same instant, read by the tenant that owns it.
    var owned = await BodyOf(tp.Responses["NorthReadsTheSameRow"]);
    Equal("WO-1001", owned.GetProperty("reference").GetString());
});

await tp.Test("A row cannot be placed into another tenant.", async () =>
{
    Equal(403, (int)tp.Responses["SouthCreatesIntoNorth"].StatusCode);
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["SouthCreatesIntoNorth"]));
    Null(tp.Responses["SouthCreatesIntoNorth"].Headers.Location);
});

await tp.Test("A cross-tenant reference is unresolvable, not confirmed — the probe runs as the caller.", async () =>
{
    Equal(new[] { "unresolved-reference" }, await ViolationCodes(tp.Responses["SouthReferencesNorthsCustomer"]));
    Equal(new[] { "/customer_id" }, await ViolationPointers(tp.Responses["SouthReferencesNorthsCustomer"]));

    // The control: the identical body naming a customer of its OWN tenant is created. So the 422 is
    // the tenant of the referenced row and not the shape of the request.
    var created = await BodyOf(tp.Responses["SouthReferencesItsOwnCustomer"]);
    Equal("WO-7003", created.GetProperty("reference").GetString());
});

await tp.Test("The tenant header can only CONFIRM the key's own tenant, never widen it.", async () =>
{
    Equal(401, (int)tp.Responses["SouthAsksToActAsNorth"].StatusCode);
    Equal("https://alvo.dev/errors/unauthenticated", await ProblemType(tp.Responses["SouthAsksToActAsNorth"]));

    // The control: the same header naming the key's own tenant is honoured and reads the same rows
    // as sending no header at all. So the 401 is the requested tenant, not the presence of a header.
    Equal(
        await PageSet(tp.Responses["SouthListsWorkOrders"], "reference"),
        (await PageSet(tp.Responses["SouthConfirmsItsOwnTenant"], "reference"))
            .Where(reference => reference != "WO-7003").ToArray());
});

await tp.Test("After every attempt, tenant north's data is exactly what it was — nothing added, nothing changed.", async () =>
{
    Equal(
        new[] { "WO-1001", "WO-1002", "WO-1003", "WO-1004", "WO-1005", "WO-1006", "WO-1007" },
        await PageColumn(tp.Responses["NorthIsIntact"], "reference"));

    // The row south tried to cancel and to delete is still there, still in the state 070 left it.
    var row = await BodyOf(tp.Responses["NorthsRowIsIntact"]);
    Equal("in_progress", row.GetProperty("status").GetString());
    Equal(1, row.GetProperty("priority").GetInt32());
    Equal(Constant("tenantNorth"), row.GetProperty("tenant_id").GetString());
});
