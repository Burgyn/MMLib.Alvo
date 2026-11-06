using MMLib.Alvo.Admin.Components.Rules;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>The simulator's words for a transition verdict, and what the dashboard says when a write is refused by one.</summary>
public sealed class TransitionVerdictsTests
{
    [Theory]
    [InlineData(true, "Allowed", "a-badge--ok")]
    [InlineData(false, "Not allowed", "a-badge--warn")]
    [InlineData(null, "Depends on the record", "")]
    public void A_verdict_is_worded_as_what_the_roles_answered(bool? allowed, string answer, string tone)
    {
        var verdict = new ManagementTransitionVerdict { Name = "close", From = ["confirmed"], To = "done", Allowed = allowed };

        TransitionVerdicts.Answer(verdict).ShouldBe(answer);
        TransitionVerdicts.Tone(verdict).ShouldBe(tone);
    }

    [Fact]
    public void A_wildcard_source_reads_as_any_state() =>
        TransitionVerdicts.Move(new ManagementTransitionVerdict { Name = "reopen", From = ["*"], To = "draft" }).ShouldBe("any state → draft");

    [Fact]
    public void A_refused_move_sends_the_operator_to_the_states_tab()
    {
        var problem = AdminProblem.From(new AlvoConstraintViolationException(AlvoConstraintKind.Transition, ["status"]), ProblemSite.RecordWrite)!;

        problem.Fix.ShouldNotBeNull().ShouldContain("status");
        problem.Fix.ShouldContain("States tab");
    }
}
