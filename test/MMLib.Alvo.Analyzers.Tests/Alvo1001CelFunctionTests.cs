namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>ALVO1001 in a CEL function: a synchronous function must not start a task it can only block on.</summary>
public sealed class Alvo1001CelFunctionTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using MMLib.Alvo;

        """;

    [Fact]
    public async Task A_task_returning_call_in_a_cel_function_is_reported()
    {
        const string source = Usings + """
            public static class Waits
            {
                public static IAlvoBuilder AddWaits(this IAlvoBuilder alvo) =>
                    alvo.AddCelFunction("later", (string s) => Task.FromResult(s).Result); // ALVO1001
            }
            """;

        var reported = await AnalyzerHarness.Alvo1001Async(source);

        AnalyzerHarness.Lines(reported).ShouldBe(AnalyzerHarness.MarkedLines(source));
        reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("'Task.FromResult' returns a task");
    }

    [Fact]
    public async Task A_pure_cel_function_with_a_token_is_not_reported() =>
        (await AnalyzerHarness.Alvo1001Async(Usings + """
            public static class Pure
            {
                public static IAlvoBuilder AddPure(this IAlvoBuilder alvo) =>
                    alvo.AddCelFunction("upper", (string s, CancellationToken ct) => ct.IsCancellationRequested ? s : s.ToUpperInvariant());
            }
            """)).ShouldBeEmpty();
}
