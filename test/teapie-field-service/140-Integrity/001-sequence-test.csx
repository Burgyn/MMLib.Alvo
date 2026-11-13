#load "../_shared/Rows.csx"

string Year() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
    TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava")).Year.ToString();

async Task<int> SequenceNumber(HttpResponseMessage created)
{
    var number = (await BodyOf(created)).GetProperty("job_number").GetString();
    StartsWith($"JOB-{Year()}-", number);
    return int.Parse(number.Substring(number.LastIndexOf('-') + 1));
}

await tp.Test("The seed is numbered 1..7 in creation order, per tenant, in Bratislava's year.", async () =>
{
    var numbers = await PageColumn(tp.Responses["ListSeededNorth"], "job_number");
    Equal(Enumerable.Range(1, 7).Select(n => $"JOB-{Year()}-{n:00000}").ToArray(), numbers);

    // South counts on its own: its first order is number one, not number eight.
    Equal(new[] { $"JOB-{Year()}-00001" }, await PageColumn(tp.Responses["ReadSouthSeed"], "job_number"));
});

await tp.Test("A create refused by a CHECK gives its number back: the next create is exactly +1.", async () =>
{
    var a = await SequenceNumber(tp.Responses["NumberedA"]);
    var b = await SequenceNumber(tp.Responses["NumberedB"]);
    Equal(a + 1, b);

    // The control that makes the equality mean something: the create between them really was attempted
    // and refused by the database's CHECK, not by an earlier gate that never reached the sequence.
    Equal(new[] { "check" }, await ViolationCodes(tp.Responses["RefusedByCheck"]));
    Equal(new[] { "/priority" }, await ViolationPointers(tp.Responses["RefusedByCheck"]));
});

await tp.Test("A create that sends job_number is refused 403: Alvo writes it.", async () =>
{
    Equal(403, (int)tp.Responses["SendsJobNumber"].StatusCode);
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["SendsJobNumber"]));
});
