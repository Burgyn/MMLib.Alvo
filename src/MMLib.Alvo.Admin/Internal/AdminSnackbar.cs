using MudBlazor;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>The dashboard's one way to say an action worked (spec §3.3).</summary>
/// <remarks>
/// <para>
/// There is deliberately no <c>Refuse</c> beside it: an error is an <c>AlvoAlert</c> in place, never a snackbar
/// that disappears with the fix it carried (design §5.5).
/// </para>
/// <para>
/// <b>Every rule is said here, per message, and none in the library's options.</b> Those options are one object per
/// container, so setting them in <c>AddAlvoAdmin</c> moved an embedding host's own snackbars too (final review I6).
/// The duration, the close button and the duplicate rule travel with each message; "at most two" is kept by removing
/// the oldest of this circuit's own messages; the bottom-left place is the provider's class in <c>alvo.css</c>.
/// </para>
/// </remarks>
internal static class AdminSnackbar
{
    /// <summary>How many confirmations are on screen at once (spec §3.3).</summary>
    internal const int MostShown = 2;

    /// <summary>How long a confirmation stays, in milliseconds (spec §3.3: 4–6 s).</summary>
    internal const int ShownFor = 5000;

    /// <summary>Shows a short confirmation of what just happened, such as "Saved to the working copy".</summary>
    public static void Confirm(this ISnackbar snackbar, string message)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        if (snackbar.Add(message, Severity.Success, Configure) is null)
        {
            return;
        }

        foreach (var older in snackbar.ShownSnackbars.SkipLast(MostShown).ToArray())
        {
            snackbar.Remove(older);
        }
    }

    /// <summary>The options one confirmation carries, whatever the host set for its own snackbars.</summary>
    /// <param name="options">The message's options, seeded from the library's shared configuration.</param>
    internal static void Configure(SnackbarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.VisibleStateDuration = ShownFor;
        options.ShowCloseIcon = true;
        options.DuplicatesBehavior = SnackbarDuplicatesBehavior.Prevent;
    }
}
