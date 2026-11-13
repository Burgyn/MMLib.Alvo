#load "../_shared/Rows.csx"

// state-machines.mdx: "In a batch, the refused row carries code `transition` (or `forbidden`) at its
// index, and the batch writes nothing. A batch refused only by transitions answers 422."

await tp.Test("The batch is 422 and the refused index — and only it — carries `transition`.", async () =>
{
    var refused = tp.Responses["BatchWithAnIllegalMove"];
    Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
    Equal(new[] { "transition" }, await ViolationCodes(refused));

    var pointers = await ViolationPointers(refused);
    True(pointers.All(p => p.StartsWith("/rows/1", StringComparison.Ordinal)),
        $"A violation pointed elsewhere than row 1. Pointers: {string.Join(", ", pointers)}");
});

await tp.Test("No row moved: the legal `start` beside the illegal move was not written either.", async () =>
{
    var rows = (await BodyOf(tp.Responses["ReadBothBack"])).GetProperty("items").EnumerateArray()
        .Select(row => row.GetProperty("reference").GetString() + "=" + row.GetProperty("status").GetString())
        .ToArray();
    Equal(new[] { "WO-3111=scheduled", "WO-3112=scheduled" }, rows);
});
