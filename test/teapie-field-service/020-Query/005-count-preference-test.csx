#load "../_shared/Rows.csx"

long? Count(System.Text.Json.JsonElement body) =>
    body.GetProperty("count").ValueKind == System.Text.Json.JsonValueKind.Null
        ? null
        : body.GetProperty("count").GetInt64();

async Task<long?> CountOf(string request) => Count(await BodyOf(tp.Responses[request]));

bool HasApplied(string request) =>
    tp.Responses[request].Headers.TryGetValues("Preference-Applied", out var _);

string AppliedOf(string request) =>
    tp.Responses[request].Headers.GetValues("Preference-Applied").Single();

await tp.Test("An exact count is the size of the matching set, not of the page.", async () =>
{
    Equal(2, await PageCount(tp.Responses["CountExact"]));
    Equal(7L, await CountOf("CountExact"));
    Equal("count=exact", AppliedOf("CountExact"));
});

await tp.Test("The count is narrowed by the caller's own filter, exactly as the rows are.", async () =>
{
    // Compared with the same filter read in full rather than with a literal, so this cannot pass because
    // the seed happened to hold that many scheduled rows.
    var whole = await PageCount(tp.Responses["FilteredWithoutCount"]);

    Equal((long)whole, await CountOf("CountFiltered"));
    NotEqual(7L, await CountOf("CountFiltered"));
});

await tp.Test("The count does not shrink as the walk advances.", async () =>
    Equal(7L, await CountOf("CountOnPageTwo")));

await tp.Test("`count=planned` degrades to an exact count, and Preference-Applied says so.", async () =>
{
    Equal(7L, await CountOf("CountPlanned"));
    Equal("count=exact", AppliedOf("CountPlanned"));
});

await tp.Test("A request that asks for no count gets one that is present and null, and no applied header.", async () =>
{
    var body = await BodyOf(tp.Responses["NoCountAsked"]);

    True(body.TryGetProperty("count", out var _));
    Null(Count(body));
    False(HasApplied("NoCountAsked"));
});

await tp.Test("An unrecognised preference is ignored rather than refused, per RFC 7240 §2.", async () =>
{
    Null(await CountOf("UnrecognisedPreference"));
    False(HasApplied("UnrecognisedPreference"));
});

await tp.Test("The count is over the POLICY-FILTERED set, so it is not a disclosure channel.", async () =>
{
    // A count taken over the bare table would report 7 to a technician who can read fewer rows: no row
    // crosses the boundary, only the number does — which every row-level fact in this suite would miss.
    var visible = (long)await PageCount(tp.Responses["CountAsTechnician"]);

    Equal(visible, await CountOf("CountAsTechnician"));
    NotEqual(7L, await CountOf("CountAsTechnician"));
});
