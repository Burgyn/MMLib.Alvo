#load "../_shared/Rows.csx"

await tp.Test("KNOWN DEFECT: a `unique` field on a scoped entity is unique across ALL tenants, so it answers tenant B a question about tenant A.", async () =>
{
    var collides = tp.Responses["ProbeAValueAnotherTenantHolds"];
    var free = tp.Responses["ProbeAValueNoTenantHolds"];

    // THE assertion, and it is deliberately about DISTINGUISHABILITY rather than about a status.
    // The two requests differ in exactly one thing — whether the other tenant holds the value — so
    // any observable difference between the answers is that fact, disclosed.
    //
    // Mapping the underlying constraint violation to a clean 409 (the sibling defect, pinned by
    // 030-Problems/002) leaves this assertion GREEN and the leak fully intact: 409-versus-201 is
    // the same signal as 500-versus-201. The fix is a tenant-scoped unique index. When that lands,
    // both are 201, this goes red, and it should be rewritten as `Equal(free.StatusCode, ...)`.
    NotEqual(free.StatusCode, collides.StatusCode);

    // Stated as the security property so the next reader sees what is actually wrong, not a number.
    Equal(201, (int)free.StatusCode);
    NotEqual(201, (int)collides.StatusCode);
});

await tp.Test("The whole refusal is distinguishable, not merely its status — so no phrasing of the answer hides it.", async () =>
{
    // Compared as complete documents. Even if the two were made to share a status, a differing
    // detail, slug or violation list would still answer the question — this is what would have to
    // become equal for the leak to be closed by anything other than the index fix.
    NotEqual(
        await RefusalFingerprint(tp.Responses["ProbeAValueNoTenantHolds"]),
        await RefusalFingerprint(tp.Responses["ProbeAValueAnotherTenantHolds"]));
});

await tp.Test("Neither probe leaked tenant north's row, and neither changed it.", async () =>
{
    // The oracle answers "does a row with this reference exist somewhere" and nothing more: no id,
    // no field, no tenant. That bound matters, and it is what keeps this a one-bit leak.
    var raw = await tp.Responses["ProbeAValueAnotherTenantHolds"].Content.ReadAsStringAsync();
    DoesNotContain(Constant("Wo1001Id"), raw);
    DoesNotContain(Constant("tenantNorth"), raw);
    DoesNotContain("Boiler service", raw);

    var north = await BodyOf(tp.Responses["NorthsRowAfterTheProbe"]);
    Equal("WO-1001", north.GetProperty("reference").GetString());
    Equal(Constant("tenantNorth"), north.GetProperty("tenant_id").GetString());
    Equal("in_progress", north.GetProperty("status").GetString());
});

await tp.Test("The refused probe wrote no row into tenant south either.", async () =>
{
    var south = await PageSet(tp.Responses["SouthAfterTheProbe"], "reference");

    // WO-1001 is north's and must not appear in south's set under any circumstances.
    DoesNotContain("WO-1001", south);

    // Only the second probe's row was added.
    Contains("WO-9911", south);
});
