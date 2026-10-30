using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>How the simulator words a command's verdict — kept out of the markup so it can be tested.</summary>
internal static class CommandVerdicts
{
    /// <summary>What the rule answered, in words.</summary>
    public static string Answer(ManagementCommandVerdict verdict) => verdict.Allowed switch
    {
        true => "admits",
        false => "refuses",
        null => "decided per row",
    };

    /// <summary>The badge's tone class.</summary>
    public static string Tone(ManagementCommandVerdict verdict) => verdict.Allowed switch
    {
        true => "a-badge--ok",
        false => "a-badge--warn",
        null => string.Empty,
    };
}
