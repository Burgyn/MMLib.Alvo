using MMLib.Alvo.Admin.Components.DesignSystem;
using MudBlazor;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>MudAlert renders no role at all (study §4.1); the wrapper gives every tone the right one.</summary>
public sealed class AlertLookTests
{
    [Theory]
    [InlineData(AlvoAlert.AlertTone.Error, "alert", Severity.Error)]
    [InlineData(AlvoAlert.AlertTone.Warning, "alert", Severity.Warning)]
    [InlineData(AlvoAlert.AlertTone.Info, "status", Severity.Info)]
    [InlineData(AlvoAlert.AlertTone.Success, "status", Severity.Success)]
    public void A_tone_has_a_role_and_a_severity(AlvoAlert.AlertTone tone, string role, Severity severity)
    {
        AlertLook.RoleOf(tone).ShouldBe(role);
        AlertLook.SeverityOf(tone).ShouldBe(severity);
    }
}
