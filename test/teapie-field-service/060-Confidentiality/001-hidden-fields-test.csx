#load "../_shared/Rows.csx"

await tp.Test("Neither hidden field appears on any row of a list, a single read, or a write's echo.", async () =>
{
    var listKeys = await PageKeys(tp.Responses["ListEveryRow"]);
    var readKeys = await KeysOf(tp.Responses["ReadOneRow"]);
    var echoKeys = await KeysOf(tp.Responses["WriteEchoesTheRow"]);

    foreach (var keys in new[] { listKeys, readKeys, echoKeys })
    {
        DoesNotContain("internal_notes", keys);
        DoesNotContain("access_code", keys);

        // The control: the mask drops exactly two names and leaves the rest of the row alone, so
        // "the response was empty" is not what the two assertions above measured.
        Contains("reference", keys);
        Contains("external_ref", keys);
    }

    // PageKeys unions across all seven rows, and the seed gave every one of them a value for both
    // hidden fields — so one leaking row would be caught, not averaged away.
    Equal(7, await PageCount(tp.Responses["ListEveryRow"]));
});

await tp.Test("No hidden VALUE reaches the wire either, under any key.", async () =>
{
    foreach (var name in new[] { "ListEveryRow", "ReadOneRow", "WriteEchoesTheRow" })
    {
        var raw = await tp.Responses[name].Content.ReadAsStringAsync();

        // The values the seed wrote into `internal_notes` and `access_code`. A body carrying one of
        // them under some other key would defeat the key-name assertions above.
        DoesNotContain("Customer disputes", raw);
        DoesNotContain("Out-of-hours rate", raw);
        DoesNotContain("ACME-1001", raw);
        DoesNotContain("BORO-1003", raw);
    }
});

await tp.Test("A filter over a hidden field is refused BYTE-IDENTICALLY to one over an undeclared field.", async () =>
{
    // Not "both are 422", and not "both mention unavailable-field": the whole document, minus the
    // per-request traceId. One differing byte — a different code, a different fix suggestion, a
    // different detail — answers "does this entity declare internal_notes" for whoever is asking.
    Equal(
        await RefusalFingerprint(tp.Responses["FilterOverUndeclaredField"]),
        await RefusalFingerprint(tp.Responses["FilterOverHiddenField"]));

    Equal(
        await RefusalFingerprint(tp.Responses["OrderByUndeclaredField"]),
        await RefusalFingerprint(tp.Responses["OrderByHiddenField"]));

    Equal(
        await RefusalFingerprint(tp.Responses["SelectUndeclaredField"]),
        await RefusalFingerprint(tp.Responses["SelectHiddenField"]));
});

await tp.Test("The refusal names the parameter's role and never the field, in all three places.", async () =>
{
    Equal(new[] { "unavailable-field" }, await ViolationCodes(tp.Responses["FilterOverHiddenField"]));
    Equal(new[] { "filter" }, await ViolationPointers(tp.Responses["FilterOverHiddenField"]));
    Equal(new[] { "order" }, await ViolationPointers(tp.Responses["OrderByHiddenField"]));
    Equal(new[] { "select" }, await ViolationPointers(tp.Responses["SelectHiddenField"]));

    foreach (var name in new[] { "FilterOverHiddenField", "OrderByHiddenField", "SelectHiddenField" })
    {
        var raw = await tp.Responses[name].Content.ReadAsStringAsync();
        DoesNotContain("internal_notes", raw);
        DoesNotContain("access_code", raw);
    }
});

await tp.Test("A filter over a declared, visible field is served — so the refusals above are about the mask.", async () =>
    Equal(new[] { "WO-1001" }, await PageSet(tp.Responses["FilterOverVisibleField"], "reference")));

await tp.Test("A hidden field is still WRITABLE: hidden restricts reading, readOnly restricts writing.", async () =>
{
    // The write returned 200 and moved the row's version, which is the only evidence available —
    // the value itself can never be read back, by construction.
    var before = tp.Responses["ReadOneRow"].Headers.ETag;
    var after = tp.Responses["AfterTheHiddenWrite"].Headers.ETag;

    NotNull(before);
    NotNull(after);
    NotEqual(before.Tag, after.Tag);

    // And nothing else about the row changed, so the version moved for the hidden write and not
    // for some other field the PATCH happened to touch.
    var row = await BodyOf(tp.Responses["AfterTheHiddenWrite"]);
    Equal("WO-1001", row.GetProperty("reference").GetString());
    Equal("Boiler service", row.GetProperty("title").GetString());
});
