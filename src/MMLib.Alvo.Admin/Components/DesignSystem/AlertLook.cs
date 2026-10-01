using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>The role and the library severity of each <see cref="AlvoAlert.AlertTone"/>.</summary>
internal static class AlertLook
{
    /// <summary><c>alert</c> for what went or may go wrong, <c>status</c> for what is merely so.</summary>
    public static string RoleOf(AlvoAlert.AlertTone tone)
        => tone is AlvoAlert.AlertTone.Error or AlvoAlert.AlertTone.Warning ? "alert" : "status";

    /// <summary>The library's severity for <paramref name="tone"/>.</summary>
    public static Severity SeverityOf(AlvoAlert.AlertTone tone) => tone switch
    {
        AlvoAlert.AlertTone.Error => Severity.Error,
        AlvoAlert.AlertTone.Warning => Severity.Warning,
        AlvoAlert.AlertTone.Success => Severity.Success,
        _ => Severity.Info,
    };
}
