#load "../_shared/Rows.csx"

// The transactional batch (#106), against real PostgreSQL. The claims here are the ones only a real
// engine can carry: that the transaction rolls back for real, and that a cross-tenant refusal comes
// from the row predicate rather than from anything the test arranged.

await tp.Test("A batch of three writes three rows, in the order they were sent.", async () =>
{
    var body = await BodyOf(tp.Responses["BatchCreateThree"]);

    Equal(3, body.GetProperty("affected").GetInt32());
    Equal(
        new[] { "WO-2001", "WO-2002", "WO-2003" },
        body.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("reference").GetString()).ToArray());
});

await tp.Test("A batch whose last row is refused leaves the first two unwritten.", async () =>
{
    var before = await PageSet(tp.Responses["CountAfterSuccess"], "reference");
    var after = await PageSet(tp.Responses["CountAfterRefusal"], "reference");

    // A set comparison rather than a count: a store that wrote WO-2101 and WO-2102 and happened to
    // lose two others would keep the count identical while having committed part of the batch.
    Equal(before, after);

    // And the control that keeps this from passing on a batch route that writes nothing at all: the
    // batch above DID write, so `before` is not the pre-batch state.
    Contains("WO-2001", before);
    DoesNotContain("WO-2101", after);
});

await tp.Test("The refusal names the offending row, so a caller can repair it in one round trip.", async () =>
{
    var pointers = await ViolationPointers(tp.Responses["BatchWithABadLastRow"]);

    True(
        pointers.Any(pointer => pointer.StartsWith("/rows/2", StringComparison.Ordinal)),
        $"No violation pointed at row 2. Pointers: {string.Join(", ", pointers)}");
    False(
        pointers.Any(pointer => pointer.StartsWith("/rows/0", StringComparison.Ordinal)
            || pointer.StartsWith("/rows/1", StringComparison.Ordinal)),
        $"A good row was reported as bad. Pointers: {string.Join(", ", pointers)}");
});

await tp.Test("A batch naming another tenant's row writes nothing, and that row is untouched.", async () =>
{
    var row = await BodyOf(tp.Responses["NorthReadsWo1001"]);

    // Read back AS ITS OWN TENANT, which is stronger than a count: it compares the value.
    Equal("Boiler service", row.GetProperty("title").GetString());
});

await tp.Test("An empty batch is refused rather than answered as a delete of nothing.", async () =>
{
    var codes = await ViolationCodes(tp.Responses["EmptyBatchDelete"]);

    // RFC 9110 9.3.5 leaves a DELETE's body undefined, so an intermediary is permitted to strip it.
    // Read as "no rows to delete" that would be a silent success for a request that never arrived.
    Contains("empty-batch", codes);
});
