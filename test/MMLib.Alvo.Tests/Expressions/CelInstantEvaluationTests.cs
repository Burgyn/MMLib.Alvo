using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A timer's <c>at</c> fails closed: a timer is never armed at a time its expression did not give.</summary>
public sealed class CelInstantEvaluationTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_throwing_host_function_in_at_escapes_as_a_function_failure()
    {
        var boom = Host("boom", _ => throw new FormatException("host secret"));

        var failure = Should.Throw<CelFunctionException>(() => Evaluate("boom(name)", boom));

        failure.FunctionName.ShouldBe("boom");
        failure.IsHost.ShouldBeTrue();
        failure.InnerException.ShouldBeOfType<FormatException>();
        failure.Message.ShouldNotContain("host secret");
    }

    [Fact]
    public void A_host_function_answering_something_that_is_not_a_timestamp_is_refused_not_armed() =>
        Should.Throw<CelFunctionException>(() => Evaluate("soon(name)", Host("soon", _ => "tomorrow")));

    [Fact]
    public void A_host_function_answering_a_timestamp_gives_its_instant() =>
        Evaluate("soon(name)", Host("soon", _ => _now.AddHours(2))).ShouldBe(_now.AddHours(2));

    private static CelFunction Host(string name, Func<object?[], object?> body) =>
        TestCelFunctions.Host(name, CelValueType.Timestamp, body, TestCelFunctions.Parameter("s", CelValueType.String));

    private static DateTimeOffset? Evaluate(string source, CelFunction function)
    {
        var result = TestCelFunctions.Compiler(function).CompileValue(source, CelProfile.Condition, TestCelFunctions.Items, CelValueType.Timestamp);
        result.IsSuccess.ShouldBeTrue(string.Join(" | ", result.Errors.Select(error => error.Message)));
        return CelInterpreter.EvaluateInstant(result.Expression!, CelFixtures.Row(("name", "x")), AlvoContext.System(null), _now);
    }
}
