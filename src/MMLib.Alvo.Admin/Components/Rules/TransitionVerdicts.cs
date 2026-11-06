using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>How the simulator words a transition's verdict — kept out of the markup so it can be tested.</summary>
internal static class TransitionVerdicts
{
    /// <summary>What the transition's roles answered, in words.</summary>
    public static string Answer(ManagementTransitionVerdict verdict) => verdict.Allowed switch
    {
        true => "Allowed",
        false => "Not allowed",
        null => "Depends on the record",
    };

    /// <summary>The badge's tone class.</summary>
    public static string Tone(ManagementTransitionVerdict verdict) => verdict.Allowed switch
    {
        true => "a-badge--ok",
        false => "a-badge--warn",
        null => string.Empty,
    };

    /// <summary>The move, as <c>from → to</c>, with <c>*</c> read as any state.</summary>
    public static string Move(ManagementTransitionVerdict verdict) =>
        $"{(verdict.From is ["*"] ? "any state" : string.Join(", ", verdict.From))} → {verdict.To}";
}
