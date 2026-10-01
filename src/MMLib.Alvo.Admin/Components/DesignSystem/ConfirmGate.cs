namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>When a destructive confirm's button may run (spec §3.2).</summary>
internal static class ConfirmGate
{
    /// <summary>
    /// Allowed, not busy, and, when a name must be typed, typed exactly. An ordinal comparison with no trimming:
    /// "almost the name" is exactly the slip the step exists to catch.
    /// </summary>
    public static bool CanConfirm(string? expected, string typed, bool busy, bool allowed)
        => allowed && !busy && (expected is null || string.Equals(typed, expected, StringComparison.Ordinal));
}
