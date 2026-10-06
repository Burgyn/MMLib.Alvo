using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Whether the Import page may load a paste into the working copy: the button's enablement and the submit's guard, one rule.
/// </summary>
/// <remarks>
/// <b>Only over a loaded copy.</b> Until the page has read the applied descriptor, or while it shows the problem that stopped
/// it, the working copy may not hold what the operator staged — it may not have been read at all — so an import would
/// replace edits nobody was asked about, the very loss <see cref="CopyReplacement"/> exists to ask about. The keyboard
/// chord reaches the submit without the button, which is why the page checks this in both places: the button by what the
/// box says it holds (<see cref="Offered"/>), the submit by the text it read (<see cref="Ready"/>), since the box's text
/// reaches the server only on submit (<see cref="ImportStream"/>).
/// </remarks>
internal static class ImportGate
{
    /// <summary>Whether an import may run now.</summary>
    /// <param name="descriptor">The applied descriptor the page read, or <see langword="null"/> while it has not.</param>
    /// <param name="problem">The problem the page shows, if any.</param>
    /// <param name="pasted">The box's text.</param>
    /// <returns><see langword="true"/> when the copy is loaded and something is pasted.</returns>
    public static bool Ready(ManagementDescriptor? descriptor, AdminProblem? problem, string pasted)
        => Offered(descriptor, problem, pasted.Trim().Length > 0);

    /// <summary>Whether the button offers an import, by what the box says it holds.</summary>
    /// <param name="descriptor">The applied descriptor the page read, or <see langword="null"/> while it has not.</param>
    /// <param name="problem">The problem the page shows, if any.</param>
    /// <param name="filled">Whether the box holds more than whitespace (<see cref="BoxMeasure.Filled"/>).</param>
    /// <returns><see langword="true"/> when the copy is loaded and something is pasted.</returns>
    public static bool Offered(ManagementDescriptor? descriptor, AdminProblem? problem, bool filled)
        => Loaded(descriptor, problem) && filled;

    /// <summary>Whether the page has loaded what an import replaces — also drawn on the form, for whoever waits on it.</summary>
    /// <param name="descriptor">The applied descriptor the page read, or <see langword="null"/> while it has not.</param>
    /// <param name="problem">The problem the page shows, if any.</param>
    /// <returns><see langword="true"/> when the descriptor was read and nothing went wrong.</returns>
    public static bool Loaded(ManagementDescriptor? descriptor, AdminProblem? problem)
        => descriptor is not null && problem is null;
}
