using MudBlazor;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>The dashboard's one way to say an action worked (spec §3.3).</summary>
/// <remarks>
/// There is deliberately no <c>Refuse</c> beside it: an error is an <c>AlvoAlert</c> in place, never a snackbar
/// that disappears with the fix it carried (design §5.5).
/// </remarks>
internal static class AdminSnackbar
{
    /// <summary>Shows a short confirmation of what just happened, such as "Saved to the working copy".</summary>
    public static void Confirm(this ISnackbar snackbar, string message)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        snackbar.Add(message, Severity.Success);
    }
}
