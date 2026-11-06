using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// 422 <c>expression-failed</c> is a Data API refusal every host answers, and only of values the caller sent in this request
/// (Ruling 44); a host's failure, a command's own write, a value the request did not carry and an aggregate holding any
/// leaf that is not such a refusal never are.
/// </summary>
public sealed class ExpressionFailedAnswerTests
{
    private static readonly CelFunctionException _intOfText = IntOfText().JudgedBy(New("quantity_text"));

    private static readonly HashSet<string> _sent = new(StringComparer.Ordinal) { "code", "quantity_text" };

    [Fact]
    public async Task A_built_in_refusal_is_a_422_naming_the_function_and_its_reason()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.RefusalFor(_intOfText, _sent).ShouldNotBeNull());

        status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        document["type"]!.GetValue<string>().ShouldBe("https://alvo.dev/errors/expression-failed");
        document["detail"]!.GetValue<string>().ShouldBe(
            "The CEL function 'int' refused this request's data: the text is not a whole number such as 42 or -7. Nothing was written.");
        document.ContainsKey("violations").ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrapped_refusal_is_found_through_its_wrapper() =>
        (await ProblemWriting.WrittenAsync(ProblemResultFactory.RefusalFor(new AggregateException(new InvalidOperationException("w", _intOfText)), _sent).ShouldNotBeNull()))
            .Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);

    [Fact]
    public void An_aggregate_that_also_holds_a_host_failure_is_not_422() =>
        ProblemResultFactory.RefusalFor(new AggregateException(_intOfText, new CelFunctionException("rate", isHost: true, new FormatException())), _sent)
            .ShouldBeNull();

    /// <summary>A storage failure beside the caller's refusal is no refusal of theirs, and must reach the log (m1).</summary>
    [Fact]
    public void An_aggregate_that_also_holds_a_failure_that_is_no_function_s_is_not_422() =>
        ProblemResultFactory.RefusalFor(new AggregateException(_intOfText, new InvalidOperationException("db")), _sent).ShouldBeNull();

    [Fact]
    public void A_command_s_own_write_is_never_422() =>
        ProblemResultFactory.PortRefusalFor(_intOfText).ShouldBeNull("PortRefusalFor is the command filter's mapping (Ruling 27)");

    [Fact]
    public async Task A_condition_with_no_bool_answers_its_own_sentence()
    {
        var failure = new CelFunctionException(CelInterpreter.WholeCondition, CelInterpreter.WholeConditionReason).JudgedBy(New("code"));

        (await ProblemWriting.WrittenAsync(ProblemResultFactory.RefusalFor(failure, _sent).ShouldNotBeNull())).Document["detail"]!.GetValue<string>().ShouldBe(
            "The hook's condition evaluated to a present value that is not a Bool, so nothing was written. Correct the values it reads.");
    }

    [Fact]
    public void A_hook_failure_and_a_host_function_failure_are_never_a_port_refusal()
    {
        ProblemResultFactory.RefusalFor(new HookFailedException("guard", HookFailedException.Exception), _sent).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException("rate", isHost: true, new FormatException()), _sent).ShouldBeNull();
        ProblemResultFactory.RefusalFor(new CelFunctionException(CelInterpreter.HookEvaluation, CelInterpreter.HookEvaluationReason, new InvalidCastException()), _sent)
            .ShouldBeNull();
        ProblemResultFactory.RefusalFor(_intOfText.NotTheCallers(), _sent).ShouldBeNull("a refusal of a value a hook wrote is not the caller's");
    }

    /// <summary>
    /// <c>new.</c> reaches the stored row on an update and a default on a create: a field the body did not name is not the
    /// caller's to fix (Ruling 44).
    /// </summary>
    [Fact]
    public void A_refusal_of_a_field_the_request_did_not_send_is_not_422()
    {
        ProblemResultFactory.RefusalFor(IntOfText().JudgedBy(New("note")), _sent).ShouldBeNull();
        ProblemResultFactory.RefusalFor(IntOfText().JudgedBy(new CelBinary(CelBinaryOperator.Add, New("code"), New("note"))), _sent)
            .ShouldBeNull("one unsent field among the read ones is enough");
    }

    /// <summary>A request with no body — a delete — sent nothing, so nothing refused is the caller's (Ruling 44).</summary>
    [Fact]
    public void A_refusal_on_a_request_with_no_body_is_not_422() =>
        ProblemResultFactory.RefusalFor(_intOfText, sent: null).ShouldBeNull();

    /// <summary>A literal or <c>@user</c> is the descriptor's or the identity provider's, never the caller's (Ruling 44, m2).</summary>
    [Fact]
    public void A_refusal_that_reads_no_field_is_not_422()
    {
        ProblemResultFactory.RefusalFor(IntOfText().JudgedBy(new CelLiteral(CelValueType.String, "x")), _sent).ShouldBeNull();
        ProblemResultFactory.RefusalFor(IntOfText().JudgedBy(new CelContextRef(CelContextValue.UserId, CelValueType.String)), _sent).ShouldBeNull();
        ProblemResultFactory.RefusalFor(IntOfText(), _sent).ShouldBeNull("no node judged it, so nothing says what it read");
    }

    private static CelFunctionException IntOfText() => new("int", "the text is not a whole number such as 42 or -7");

    private static CelFieldRef New(string field) => new(field, CelValueType.String, CelRecordState.New);
}
