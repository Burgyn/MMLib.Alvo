using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The gate the hook sheet asks before it submits: the rows on screen answer when asked (ruling S-B).</summary>
public class ConditionGateTests
{
    [Fact]
    public void With_no_rows_attached_nothing_is_refused()
        => new ConditionGate().Refusal.ShouldBeNull();

    [Fact]
    public void The_answer_is_asked_at_the_moment_not_remembered()
    {
        var gate = new ConditionGate();
        string? answer = "Condition 1 needs a value.";
        gate.Attach(() => answer);
        gate.Refusal.ShouldBe("Condition 1 needs a value.");

        answer = null;

        gate.Refusal.ShouldBeNull("a pushed refusal would have gone stale here");
    }

    [Fact]
    public void The_rows_drawn_last_are_the_ones_asked()
    {
        var gate = new ConditionGate();
        gate.Attach(() => "old");

        gate.Attach(() => null);

        gate.Refusal.ShouldBeNull();
    }
}
