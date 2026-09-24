using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>One submit at a time — the double click that created two people (inventory defect #6).</summary>
public sealed class SubmitGateTests
{
    [Fact]
    public void A_second_submit_is_refused_while_the_first_is_in_flight()
    {
        var gate = new SubmitGate();

        gate.TryBegin().ShouldBeTrue();
        gate.Busy.ShouldBeTrue();
        gate.TryBegin().ShouldBeFalse();
    }

    [Fact]
    public void The_gate_opens_again_when_the_submit_ends()
    {
        var gate = new SubmitGate();
        gate.TryBegin();

        gate.End();

        gate.Busy.ShouldBeFalse();
        gate.TryBegin().ShouldBeTrue();
    }
}
