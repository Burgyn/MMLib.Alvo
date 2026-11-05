using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// 422 <c>expression-failed</c> is a Data API refusal every host answers; a host's failure, a command's own write and an
/// aggregate holding any failure that is not the caller's never are.
/// </summary>
public sealed class ExpressionFailedAnswerTests
{
    private static readonly CelFunctionException _intOfText = new("int", "the text is not a whole number such as 42 or -7");

    [Fact]
    public async Task A_built_in_refusal_is_a_422_naming_the_function_and_its_reason()
    {
        var (status, document) = await WrittenAsync(ProblemResultFactory.RefusalFor(_intOfText).ShouldNotBeNull());

        status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        document["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/expression-failed");
        document["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'int' refused this request's data: the text is not a whole number such as 42 or -7. Nothing was written.");
        document.ContainsKey("violations").ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrapped_refusal_is_found_through_its_wrapper() =>
        (await WrittenAsync(ProblemResultFactory.RefusalFor(new AggregateException(new InvalidOperationException("w", _intOfText))).ShouldNotBeNull()))
            .Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);

    [Fact]
    public void An_aggregate_that_also_holds_a_host_failure_is_not_422() =>
        ProblemResultFactory.RefusalFor(new AggregateException(_intOfText, new CelFunctionException("rate", isHost: true, new FormatException())))
            .ShouldBeNull();

    [Fact]
    public void A_command_s_own_write_is_never_422() =>
        ProblemResultFactory.PortRefusalFor(_intOfText).ShouldBeNull("PortRefusalFor is the command filter's mapping (Ruling 27)");

    [Fact]
    public async Task A_condition_with_no_bool_answers_its_own_sentence()
    {
        var failure = new CelFunctionException(CelInterpreter.WholeCondition, CelInterpreter.WholeConditionReason);

        (await WrittenAsync(ProblemResultFactory.RefusalFor(failure).ShouldNotBeNull())).Document["detail"]!.GetValue<string>().ShouldBe(
            "The hook's condition evaluated to a present value that is not a Bool, so nothing was written. Correct the values it reads.");
    }

    [Fact]
    public void A_hook_failure_and_a_host_function_failure_are_never_a_port_refusal()
    {
        ProblemResultFactory.RefusalFor(new HookFailedException("guard", HookFailedException.Exception)).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException("rate", isHost: true, new FormatException())).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException(CelInterpreter.HookEvaluation, CelInterpreter.HookEvaluationReason, new InvalidCastException()))
            .ShouldBeNull();
        ProblemResultFactory.RefusalFor(_intOfText.NotTheCallers()).ShouldBeNull("a refusal of a value a hook wrote is not the caller's");
    }

    private static Task<(int Status, JsonObject Document)> WrittenAsync(IResult result) =>
        ProblemDetailsTests.DocumentWrittenByAsync(result);
}
