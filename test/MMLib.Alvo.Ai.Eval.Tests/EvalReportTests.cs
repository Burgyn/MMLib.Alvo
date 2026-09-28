namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>The suite bar: every case in every language at least two thirds of its runs, and the whole at least 90 %.</summary>
public sealed class EvalReportTests
{
    [Fact]
    public void No_runs_is_not_a_pass() => EvalReport.SuitePasses([]).ShouldBeFalse();

    [Fact]
    public void Every_group_at_two_thirds_and_the_whole_above_ninety_percent_passes() =>
        EvalReport.SuitePasses([.. Group("a", 3, 3), .. Group("b", 3, 3), .. Group("c", 3, 3), .. Group("d", 2, 3)])
            .ShouldBeTrue("11 of 12 is 91.7 %, and d holds 2 of 3");

    [Fact]
    public void One_group_below_two_thirds_fails_however_good_the_whole_is() =>
        EvalReport.SuitePasses([.. Enumerable.Range(0, 9).SelectMany(index => Group($"g{index}", 3, 3)), .. Group("low", 1, 3)])
            .ShouldBeFalse("28 of 30 is 93 %, but 'low' holds 1 of 3");

    [Fact]
    public void Every_group_at_two_thirds_still_fails_the_ninety_percent_floor() =>
        EvalReport.SuitePasses([.. Group("a", 2, 3), .. Group("b", 2, 3)]).ShouldBeFalse();

    [Fact]
    public void A_cancelled_run_prints_as_partial_and_never_as_a_pass()
    {
        using var output = new StringWriter();
        var options = new EvalOptions(
            "/repo", new AlvoAiConnection(AiConnectionKind.OpenAiCompatible, new Uri("http://127.0.0.1:9/v1"), "m", null), 1, null, ["en"]);

        EvalReport.Print(options, Group("a", 1, 1), output, cancelled: true);

        output.ToString().ShouldContain("CANCELLED after 1 turn(s), partial");
        output.ToString().ShouldContain("— FAIL");
    }

    private static CaseRun[] Group(string name, int passed, int total) =>
    [
        .. Enumerable.Range(0, total).Select(index => new CaseRun(name, "en", Turns.Turn(), new Verdict(index < passed, "why"))),
    ];
}
