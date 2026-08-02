#load "../_shared/Rows.csx"

await tp.Test("Tenant A's session did what it set out to do.", async () =>
{
    var set = await BodyOf(tp.Responses["ASetBefore"]);
    var rows = set.GetProperty("items").EnumerateArray().ToArray();

    Equal(2, rows.Length);
    Equal(new[] { "WO-8501", "WO-8502" }, rows.Select(r => r.GetProperty("reference").GetString()).ToArray());
    Equal("in_progress", rows[0].GetProperty("status").GetString());
    Equal(131.00m, rows[0].GetProperty("quoted_price").GetDecimal());
});

await tp.Test("Tenant B cannot SEE any of it — and its own rows prove the reads are working.", async () =>
{
    var jobs = await PageSet(tp.Responses["BListsWorkOrders"], "reference");
    var customers = await PageColumn(tp.Responses["BListsCustomers"], "name");

    DoesNotContain("WO-8501", jobs);
    DoesNotContain("WO-8502", jobs);
    DoesNotContain("Journey Holdings", customers);

    // Non-empty, so "B sees nothing" is not the explanation. B's own set is there.
    Contains("WO-5001", jobs);
    Contains("Southern Foods", customers);
});

await tp.Test("Tenant B cannot INFER their existence: a filter naming A's row narrows to nothing.", async () =>
{
    Equal(200, (int)tp.Responses["BFiltersForAsJob"].StatusCode);
    Equal(0, await PageCount(tp.Responses["BFiltersForAsJob"]));
});

await tp.Test("Tenant B cannot read, update, delete, or condition a write on A's rows — every answer is 404.", async () =>
{
    foreach (var name in new[]
    {
        "BReadsAsJob", "BReadsAsCustomer", "BUpdatesAsJob", "BDeletesAsJob", "BConditionsOnAsVersion",
    })
    {
        Equal(404, (int)tp.Responses[name].StatusCode);
        Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses[name]));
    }

    // BConditionsOnAsVersion carried A's REAL, CURRENT ETag. A 412 there would have confirmed the
    // row exists and that the tag was stale or fresh — an oracle a 404 gives nothing away to. That
    // it is 404 and not 412 is the sharpest assertion in this file.
    NotEqual(412, (int)tp.Responses["BConditionsOnAsVersion"].StatusCode);
});

await tp.Test("Tenant B cannot reach A through a reference, or plant a row in A's tenant.", async () =>
{
    Equal(new[] { "unresolved-reference" }, await ViolationCodes(tp.Responses["BReferencesAsCustomer"]));
    Equal(new[] { "/customer_id" }, await ViolationPointers(tp.Responses["BReferencesAsCustomer"]));

    Equal(403, (int)tp.Responses["BCreatesIntoA"].StatusCode);
    Null(tp.Responses["BCreatesIntoA"].Headers.Location);
});

await tp.Test("Tenant B's own work still succeeds, so none of the refusals above is B being broken.", async () =>
{
    var created = await BodyOf(tp.Responses["BWorksInItsOwnTenant"]);

    Equal("WO-8603", created.GetProperty("reference").GetString());
    Equal(Constant("tenantSouth"), created.GetProperty("tenant_id").GetString());
});

await tp.Test("THE STATE OF THE WORLD — tenant A's set is field-for-field what A left behind.", async () =>
{
    var before = await BodyOf(tp.Responses["ASetBefore"]);
    var after = await BodyOf(tp.Responses["ASetAfter"]);

    // The whole rows, compared as JSON. Not a count and not a status: B's refused PATCH asked to
    // set status "cancelled", quoted_price 1.00 and title "Hijacked", and B's refused DELETE asked
    // to remove WO-8502 — a write refused with the right status and applied anyway would pass every
    // assertion above and fail exactly here.
    Equal(before.GetProperty("items").GetRawText(), after.GetProperty("items").GetRawText());

    var rows = after.GetProperty("items").EnumerateArray().ToArray();
    Equal(2, rows.Length);
    Equal("in_progress", rows[0].GetProperty("status").GetString());
    Equal(131.00m, rows[0].GetProperty("quoted_price").GetDecimal());
    Equal("Journey job one", rows[0].GetProperty("title").GetString());
    Equal(Constant("userDispatcherNorth"), rows[0].GetProperty("updated_by").GetString());

    // And the row B tried to delete is still there.
    Equal("WO-8502", rows[1].GetProperty("reference").GetString());
});

await tp.Test("Tenant A's customer is unchanged too, and still carries A's tenant.", async () =>
{
    var customer = await BodyOf(tp.Responses["AsCustomerAfter"]);

    Equal("Journey Holdings", customer.GetProperty("name").GetString());
    Equal("priority", customer.GetProperty("tier").GetString());
    Equal(Constant("tenantNorth"), customer.GetProperty("tenant_id").GetString());
});
