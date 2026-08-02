#load "../_shared/Rows.csx"

await tp.Test("A `unique` field on a scoped entity answers tenant south NOTHING about tenant north's data.", async () =>
{
    var collides = tp.Responses["ProbeAValueAnotherTenantHolds"];
    var free = tp.Responses["ProbeAValueNoTenantHolds"];

    // THE assertion, and it is deliberately about INDISTINGUISHABILITY rather than about a status. The
    // two requests differ in exactly one thing — whether the other tenant holds the value — so any
    // observable difference between the answers would be that fact, disclosed.
    //
    // It was `NotEqual` while the defect stood, because mapping the underlying constraint violation to a
    // clean 409 (the sibling defect, 030-Problems/002) left the leak fully intact: 409-versus-201 is the
    // same signal as 500-versus-201 was. Only the tenant-scoped index removes the signal, and this is the
    // equality that says so.
    Equal(free.StatusCode, collides.StatusCode);

    // Stated as the security property, so the next reader sees what is true rather than a number.
    Equal(201, (int)free.StatusCode);
    Equal(201, (int)collides.StatusCode);
});

await tp.Test("The whole answer is indistinguishable, not merely its status — so nothing else carries the bit.", async () =>
{
    // Compared as complete documents. Four fields are excluded and each exclusion is forced by the row
    // rather than chosen: `id` is assigned per row, `reference` is the one thing the two requests
    // deliberately differ in, and `created_at`/`updated_at` are stamped per write. Every other field —
    // and the key set, and the status — has to match, because a differing value, key or message would
    // answer the question the status no longer does.
    Equal(
        await CreateFingerprint(tp.Responses["ProbeAValueNoTenantHolds"]),
        await CreateFingerprint(tp.Responses["ProbeAValueAnotherTenantHolds"]));

    // And the key sets are compared whole, including the four the fingerprint drops: an answer that
    // omitted a field for one probe and carried it for the other would be the same leak.
    Equal(
        await KeysOf(tp.Responses["ProbeAValueNoTenantHolds"]),
        await KeysOf(tp.Responses["ProbeAValueAnotherTenantHolds"]));
});

await tp.Test("Uniqueness still HOLDS inside a tenant — the fix narrowed the constraint, it did not drop it.", async () =>
{
    var refused = tp.Responses["SouthRepeatsItsOwnReference"];

    Equal(409, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));
    Equal(new[] { "unique" }, await ViolationCodes(refused));
    Equal(new[] { "/reference" }, await ViolationPointers(refused));
});

await tp.Test("Neither probe read tenant north's row, and neither changed it.", async () =>
{
    // The accepted create discloses south's own new row and nothing of north's: not north's id, not
    // north's tenant, not north's title. That was the bound on the leak while it existed; it is now the
    // bound on what an accepted create says.
    var raw = await tp.Responses["ProbeAValueAnotherTenantHolds"].Content.ReadAsStringAsync();
    DoesNotContain(Constant("Wo1001Id"), raw);
    DoesNotContain(Constant("tenantNorth"), raw);
    DoesNotContain("Boiler service", raw);

    var north = await BodyOf(tp.Responses["NorthsRowAfterTheProbe"]);
    Equal("WO-1001", north.GetProperty("reference").GetString());
    Equal(Constant("tenantNorth"), north.GetProperty("tenant_id").GetString());
    Equal("in_progress", north.GetProperty("status").GetString());
});

await tp.Test("One reference, two rows — south's WO-1001 is south's own, in south's tenant.", async () =>
{
    var south = await PageSet(tp.Responses["SouthAfterTheProbe"], "reference");

    // WO-1001 is now in BOTH tenants' sets, which is the whole point: the value is unique per tenant.
    // 080-Tenancy/001 already proved south cannot read north's row of that reference, so two rows with
    // one reference is not two callers seeing one row.
    Equal(new[] { "WO-1001", "WO-5001", "WO-7003", "WO-9911" }, south);

    var created = await BodyOf(tp.Responses["ProbeAValueAnotherTenantHolds"]);
    Equal("WO-1001", created.GetProperty("reference").GetString());
    Equal(Constant("tenantSouth"), created.GetProperty("tenant_id").GetString());
    NotEqual(Constant("Wo1001Id"), created.GetProperty("id").GetString());
});

/// One create's answer with everything that identifies the ROW removed, so two creates can be compared
/// for being the same ANSWER rather than the same row. Four exclusions, all forced: `id` is assigned per
/// row, `reference` is the single field the two probes deliberately differ in, and `created_at` and
/// `updated_at` are stamped per write on this audited entity. Nothing else may differ.
async Task<string> CreateFingerprint(HttpResponseMessage response)
{
    var excluded = new[] { "id", "reference", "created_at", "updated_at" };
    var body = await BodyOf(response);
    var fields = body.EnumerateObject()
        .Where(property => !excluded.Contains(property.Name, StringComparer.Ordinal))
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .Select(property => property.Name + "=" + property.Value.GetRawText());
    return (int)response.StatusCode + "|" + string.Join("|", fields);
}
