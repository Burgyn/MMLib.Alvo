using MMLib.Alvo.Admin.Components.Rules;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>The simulator's words for a command verdict: it never claims an answer the engine did not give.</summary>
public sealed class CommandVerdictsTests
{
    [Theory]
    [InlineData(true, "admits", "a-badge--ok")]
    [InlineData(false, "refuses", "a-badge--warn")]
    [InlineData(null, "decided per row", "")]
    public void A_verdict_is_worded_as_what_the_rule_answered(bool? allowed, string answer, string tone)
    {
        var verdict = new ManagementCommandVerdict { Name = "c", Access = "true", Allowed = allowed };

        CommandVerdicts.Answer(verdict).ShouldBe(answer);
        CommandVerdicts.Tone(verdict).ShouldBe(tone);
    }
}
