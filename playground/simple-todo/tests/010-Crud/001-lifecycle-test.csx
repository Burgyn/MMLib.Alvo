#load "../../../_shared/Rows.csx"

await tp.Test("A create answers the row as stored, with a server-assigned id and the four fields we sent.", async () =>
{
    var created = await BodyOf(tp.Responses["Create"]);

    NotNull(created.GetProperty("id").GetString());
    Equal("Buy milk " + RunToken() + "-crud", created.GetProperty("title").GetString());
    Equal("todo", created.GetProperty("status").GetString());
    Equal("Two litres, semi-skimmed.", created.GetProperty("description").GetString());
    Equal("2026-08-31", created.GetProperty("due_on").GetString());
});

await tp.Test("The row carries the four declared fields plus the six the framework owns — and nothing else.", async () =>
{
    // Spelled out as a set rather than probed field by field, because the claim is about what the API
    // publishes: a field appearing here that the descriptor never declared is exactly as wrong as one
    // missing. `id` plus the five `audit: true` stamps are the framework's; drop `audit` from the
    // descriptor and the four stamps go with it.
    Equal(
        new[] { "created_at", "created_by", "description", "due_on", "id", "status", "title", "updated_at", "updated_by" },
        await KeysOf(tp.Responses["Create"]));
});

tp.Test("An audited row is versioned, so a create hands out an ETag and a conditional write needs no read first.", () =>
{
    var onCreate = tp.Responses["Create"].Headers.ETag;

    NotNull(onCreate);

    // Strong, because RFC 9110 §13.1.1 compares `If-Match` with the strong comparison function — a
    // weak tag would never match and the header would silently protect nothing.
    False(onCreate.IsWeak);
});

tp.Test("A read whose version the caller already holds is 304 with no body, not the row again.", () =>
{
    Equal(304, (int)tp.Responses["ReadUnchanged"].StatusCode);
    Equal(0, tp.Responses["ReadUnchanged"].Content.Headers.ContentLength ?? 0);
});

await tp.Test("A PATCH is partial: the field named moves and the three unmentioned ones are left alone.", async () =>
{
    var updated = await BodyOf(tp.Responses["Update"]);

    Equal("doing", updated.GetProperty("status").GetString());

    // The half a partial update is usually asserted without. A PATCH that replaced the row would
    // answer 200 with these three nulled, and a status-only assertion would call that a pass.
    Equal("Buy milk " + RunToken() + "-crud", updated.GetProperty("title").GetString());
    Equal("Two litres, semi-skimmed.", updated.GetProperty("description").GetString());
    Equal("2026-08-31", updated.GetProperty("due_on").GetString());
});

tp.Test("Every accepted write mints a NEW version, which is what makes the refusal below reachable.", () =>
{
    var read = tp.Responses["Read"].Headers.ETag;
    var afterUpdate = tp.Responses["Update"].Headers.ETag;

    // A write that returned the tag it was sent would make every later If-Match succeed, and the
    // stale-tag 412 would be unreachable — so this is what keeps that case from passing vacuously.
    NotEqual(read.Tag, afterUpdate.Tag);
});

await tp.Test("A stale precondition is refused rather than applied, and the row is not touched.", async () =>
{
    var refused = tp.Responses["UpdateWithStaleTag"];

    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));

    // The refused write asked for `done`; the row was `doing` at that moment. Read from the NEXT
    // response rather than re-requesting, so the claim is about the state the server itself reported.
    var afterRefusal = await BodyOf(tp.Responses["Finish"]);
    Equal("done", afterRefusal.GetProperty("status").GetString());
});

await tp.Test("A deleted row is 404 on read — no soft delete is declared, so there is nothing left to find.", async () =>
{
    Equal(204, (int)tp.Responses["Delete"].StatusCode);
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["ReadDeleted"]));
});
