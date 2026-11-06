using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A write its entity's state machine refuses is 422 <c>validation</c>, code <c>transition</c>, pointed at the state field,
/// and its wording names neither the stored state nor the one sent.
/// </summary>
public sealed class TransitionViolationAnswerTests
{
    private const string TransitionMessage = "This write would move a record to a state its state machine does not allow from its current one.";

    private const string TransitionFix =
        "Send a state a declared transition reaches from the current one — the entity's stateMachine lists them — or leave the field unchanged.";

    [Fact]
    public async Task A_transition_violation_is_422_validation_pointing_at_the_state_field()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Transition, ["status"])).ShouldNotBeNull());

        status.ShouldBe(422);
        document["type"]!.GetValue<string>().ShouldEndWith(AlvoProblemTypes.Validation);
        var violation = document["violations"]!.AsArray().ShouldHaveSingleItem()!;
        violation["pointer"]!.GetValue<string>().ShouldBe("/status");
        violation["code"]!.GetValue<string>().ShouldBe("transition");
    }

    [Fact]
    public async Task The_wording_names_no_state()
    {
        var (_, named) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Transition, ["status"])).ShouldNotBeNull());
        var (_, unnamed) = await ProblemWriting.WrittenAsync(ProblemResultFactory.PortRefusalFor(
            new AlvoConstraintViolationException(AlvoConstraintKind.Transition, [])).ShouldNotBeNull());

        new AlvoConstraintViolationException(AlvoConstraintKind.Transition, ["status"]).Message.ShouldBe(TransitionMessage);
        (named["violations"]![0]!["message"]!.GetValue<string>(), named["violations"]![0]!["fixSuggestion"]!.GetValue<string>())
            .ShouldBe((TransitionMessage, TransitionFix));
        (unnamed["violations"]![0]!["pointer"]!.GetValue<string>(), unnamed["violations"]![0]!["code"]!.GetValue<string>(), unnamed["violations"]![0]!["message"]!.GetValue<string>())
            .ShouldBe((string.Empty, "transition", TransitionMessage));
    }

    [Fact]
    public async Task A_batch_refused_only_by_transitions_is_422_validation_like_a_single_write()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.RowsRefused(
            [BatchViolations.FromPort(new AlvoRowRefusal(1, "transition", TransitionMessage, TransitionFix))]));

        status.ShouldBe(422);
        document["type"]!.GetValue<string>().ShouldEndWith(AlvoProblemTypes.Validation);
        var violation = document["violations"]!.AsArray().ShouldHaveSingleItem()!;
        (violation["pointer"]!.GetValue<string>(), violation["code"]!.GetValue<string>()).ShouldBe(("/rows/1", "transition"));
    }

    [Fact]
    public async Task A_batch_with_any_policy_refusal_stays_403()
    {
        var (status, document) = await ProblemWriting.WrittenAsync(ProblemResultFactory.RowsRefused(
        [
            BatchViolations.FromPort(new AlvoRowRefusal(0, "transition", TransitionMessage, TransitionFix)),
            BatchViolations.FromPort(new AlvoRowRefusal(1, "forbidden", "Refused.", null)),
        ]));

        status.ShouldBe(403);
        document["type"]!.GetValue<string>().ShouldEndWith(AlvoProblemTypes.Forbidden);
    }
}
