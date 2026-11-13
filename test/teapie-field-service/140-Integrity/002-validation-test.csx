#load "../_shared/Rows.csx"

await tp.Test("Each value a validation refuses is 422 `check` at exactly the field to fix.", async () =>
{
    foreach (var (name, pointer) in new[]
    {
        ("OpensAt25", "/service_opens_hour"),
        ("ClosesBeforeOpens", "/service_closes_hour"),
        ("PriorityZero", "/priority"),
        ("NegativeQuote", "/quoted_price"),
    })
    {
        var refused = tp.Responses[name];
        Equal(422, (int)refused.StatusCode);
        Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
        Equal(new[] { "check" }, await ViolationCodes(refused));
        Equal(new[] { pointer }, await ViolationPointers(refused));
    }
});

await tp.Test("The boundary values beside each refusal are stored as sent.", async () =>
{
    var sane = await BodyOf(tp.Responses["SaneHours"]);
    Equal(7, sane.GetProperty("service_opens_hour").GetInt32());
    Equal(19, sane.GetProperty("service_closes_hour").GetInt32());

    var closesOnly = await BodyOf(tp.Responses["ClosesOnly"]);
    Equal(System.Text.Json.JsonValueKind.Null, closesOnly.GetProperty("service_opens_hour").ValueKind);
    Equal(17, closesOnly.GetProperty("service_closes_hour").GetInt32());

    var edges = await BodyOf(tp.Responses["EdgeHours"]);
    Equal(0, edges.GetProperty("service_opens_hour").GetInt32());
    Equal(24, edges.GetProperty("service_closes_hour").GetInt32());

    var boundary = await BodyOf(tp.Responses["BoundaryValues"]);
    Equal(5, boundary.GetProperty("priority").GetInt32());
    Equal(0m, boundary.GetProperty("quoted_price").GetDecimal());
});
