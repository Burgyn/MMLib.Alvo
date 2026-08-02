#load "../_shared/Rows.csx"

tp.Test("An unaudited entity mints no ETag on any response, so there is no tag a caller could send back.", () =>
{
    Null(tp.Responses["ReadUnversioned"].Headers.ETag);
    Null(tp.Responses["UnconditionalWrite"].Headers.ETag);
    Null(tp.Responses["IfMatchAnyOnUnversioned"].Headers.ETag);
});

await tp.Test("An If-Match naming a version is refused, and the refusal says why the entity cannot answer it.", async () =>
{
    var refused = tp.Responses["IfMatchOnUnversioned"];

    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));

    var detail = (await BodyOf(refused)).GetProperty("detail").GetString();
    Contains("keeps no version of a row", detail);
    Contains("audit: true", detail);
});

await tp.Test("`If-Match: *` is accepted here, which is what makes the refusal above about the VERSION.", async () =>
{
    var written = await BodyOf(tp.Responses["IfMatchAnyOnUnversioned"]);

    Equal("Upgraded to the priority tier.", written.GetProperty("notes").GetString());
});

await tp.Test("`If-None-Match` is refused on a write, and the refusal names the deviation it is.", async () =>
{
    var refused = tp.Responses["IfNoneMatchOnWrite"];

    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));
    Contains("'If-None-Match' is evaluated on a read",
        (await BodyOf(refused)).GetProperty("detail").GetString());
});

await tp.Test("The end state carries both accepted writes and neither refused one.", async () =>
{
    var final = await BodyOf(tp.Responses["FinalUnversionedState"]);

    // UnconditionalWrite set `priority`; IfMatchAnyOnUnversioned set `notes`. Both refusals asked
    // for tier "standard", so a tier of "priority" is the proof that neither landed.
    Equal("priority", final.GetProperty("tier").GetString());
    Equal("Upgraded to the priority tier.", final.GetProperty("notes").GetString());

    // A PATCH is partial: the fields nobody mentioned kept their stored values.
    Equal("Borovica Bakery", final.GetProperty("name").GetString());
    Equal("office@borovica.test", final.GetProperty("email").GetString());
});
