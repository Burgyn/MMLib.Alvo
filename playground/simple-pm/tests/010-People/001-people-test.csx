#load "../../../_shared/Rows.csx"

// Restated rather than inherited: the shared file's own `using` covers the helpers defined there, and a
// TYPE named directly in this script needs the namespace here too.
using System.Text.Json;

// The capture comes first and asserts NOTHING, deliberately. A `tp.SetVariable` inside a failing test
// never runs, so folding the capture into an assertion would let one wrong expectation here take out
// every later case with "Variable 'AdaId' was not found" instead of reporting itself. It cannot live at
// the script's top level either: TeaPie runs a script's top-level body BEFORE the requests, so
// `tp.Responses` is empty up there.
await tp.Test("Capture: the person ids the later cases reference.", async () =>
{
    var ada = await BodyOf(tp.Responses["CreateAda"]);
    var grace = await BodyOf(tp.Responses["CreateGrace"]);
    var zoe = await BodyOf(tp.Responses["CreateAnonymousContributor"]);

    tp.SetVariable("AdaId", ada.GetProperty("id").GetString());
    tp.SetVariable("GraceId", grace.GetProperty("id").GetString());

    // Zoe is assigned nothing, which is why 040 can delete her — she is the control that separates
    // "a delete was refused by a reference" from "a delete does not work".
    tp.SetVariable("ZoeId", zoe.GetProperty("id").GetString());
});

await tp.Test("A person is created with the fields sent, and `email` is genuinely optional.", async () =>
{
    var ada = await BodyOf(tp.Responses["CreateAda"]);
    var nameless = await BodyOf(tp.Responses["CreateAnonymousContributor"]);

    Equal(RunToken() + " Ada Lovelace", ada.GetProperty("name").GetString());
    Equal("ada-" + RunToken() + "@example.test", ada.GetProperty("email").GetString());

    // Present and null rather than absent — the row has the shape the schema publishes either way.
    Equal(JsonValueKind.Null, nameless.GetProperty("email").ValueKind);
});

await tp.Test("A duplicate unique value is 409 `conflict`, and it names the field and the reason.", async () =>
{
    var refused = tp.Responses["DuplicateEmail"];

    // Not 422, and not 500. `unique` is one of two facets nothing can check BEFORE the write — only
    // the engine knows whether another row already holds the value — so it arrives as the database
    // refusing the INSERT. Reaching the caller as a 500 would say "an invariant Alvo relies on is
    // broken", which a caller picking a taken email address is not.
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));
    Equal(new[] { "unique" }, await ViolationCodes(refused));
    Equal(new[] { "/email" }, await ViolationPointers(refused));
});

await tp.Test("A malformed email is 422 `validation` — the facet the framework CAN check, checked first.", async () =>
{
    var refused = tp.Responses["NotAnEmail"];

    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
    Equal(new[] { "format" }, await ViolationCodes(refused));
    Equal(new[] { "/email" }, await ViolationPointers(refused));

    // The pair above and this one are the whole distinction worth learning here: same field, same
    // status class in spirit, two different kinds of refusal — and two different fixes.
});

await tp.Test("The team is the three accepted rows; neither refused write left one behind.", async () =>
    Equal(
        new[]
        {
            RunToken() + " Ada Lovelace",
            RunToken() + " Grace Hopper",
            RunToken() + " Zoe Nameless",
        },
        await PageColumn(tp.Responses["Team"], "name")));
