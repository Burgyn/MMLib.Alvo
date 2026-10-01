using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>When a destructive confirm may run (spec §3.2).</summary>
public sealed class ConfirmGateTests
{
    [Theory]
    [InlineData(null, "", false, true, true)]
    [InlineData(null, "", true, true, false)]
    [InlineData(null, "", false, false, false)]
    [InlineData("field-service", "", false, true, false)]
    [InlineData("field-service", "Field-Service", false, true, false)]
    [InlineData("field-service", "field-service ", false, true, false)]
    [InlineData("field-service", "field-service", false, true, true)]
    [InlineData("field-service", "field-service", true, true, false)]
    public void It_runs_only_when_allowed_not_busy_and_the_name_is_typed_exactly(
        string? expected, string typed, bool busy, bool allowed, bool runs)
        => ConfirmGate.CanConfirm(expected, typed, busy, allowed).ShouldBe(runs);
}
