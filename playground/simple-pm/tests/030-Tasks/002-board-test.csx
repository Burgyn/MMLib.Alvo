#load "../../../_shared/Rows.csx"

using System.Text.Json;

tp.Test("Each accepted write mints a new version, which is what makes the stale write below fail.", () =>
{
    var read = tp.Responses["ReadCard"].Headers.ETag;
    var afterStart = tp.Responses["Start"].Headers.ETag;
    var afterHandOver = tp.Responses["HandOver"].Headers.ETag;

    NotEqual(read.Tag, afterStart.Tag);
    NotEqual(afterStart.Tag, afterHandOver.Tag);
});

await tp.Test("The card moves todo → doing → done, and each step keeps what the last one set.", async () =>
{
    var started = await BodyOf(tp.Responses["Start"]);
    var handedOver = await BodyOf(tp.Responses["HandOver"]);

    Equal("doing", started.GetProperty("status").GetString());
    Equal(8m, started.GetProperty("estimate_hours").GetDecimal());

    Equal("done", handedOver.GetProperty("status").GetString());
    Equal(Constant("GraceId"), handedOver.GetProperty("assignee_id").GetString());

    // The estimate the earlier step set survived a PATCH that never mentioned it.
    Equal(8m, handedOver.GetProperty("estimate_hours").GetDecimal());
});

await tp.Test("The second editor's stale write is refused, and it did not revert the first one's change.", async () =>
{
    var refused = tp.Responses["SecondEditorLoses"];

    Equal("https://alvo.dev/errors/precondition-failed", await ProblemType(refused));

    // The half that matters. The refused write asked for `todo` and 40 hours; the row is `done` at
    // 8 — so nothing of it landed. This is the lost update `If-Match` exists to prevent, and without
    // the header the write would have succeeded and the first editor would never have known.
    var final = await BodyOf(tp.Responses["FinalState"]);
    Equal("done", final.GetProperty("status").GetString());
    Equal(8m, final.GetProperty("estimate_hours").GetDecimal());
});

await tp.Test("An explicit null on a nullable `ref` clears it — a PATCH's null is a value, not a silence.", async () =>
{
    var final = await BodyOf(tp.Responses["FinalState"]);

    Equal(JsonValueKind.Null, final.GetProperty("milestone_id").ValueKind);

    // And the OTHER reference, unmentioned by that PATCH, is untouched — which is what separates
    // "null was sent" from "the field was omitted".
    Equal(Constant("GraceId"), final.GetProperty("assignee_id").GetString());
});
