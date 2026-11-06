using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The gates every background dispatcher keeps before its loop claims anything: events on, the background mode, a
/// <c>Ready</c> boot with a primed catalog.
/// </summary>
/// <remarks>One sequence for the delivery dispatcher and the timer pump, so a loop never starts where the other would not.</remarks>
internal static class DispatcherGates
{
    /// <summary>Waits for the boot to settle and answers whether the loop may run, logging why it stands down.</summary>
    /// <param name="events">The dispatch mode and the events switch.</param>
    /// <param name="boot">The readiness signal.</param>
    /// <param name="catalogs">The policy catalog the boot primes.</param>
    /// <param name="logManual">Logs that the manual mode keeps the loop off.</param>
    /// <param name="logUnprimed">Logs that the boot finished without a primed catalog.</param>
    /// <param name="stop">The host's stopping token.</param>
    /// <returns><see langword="true"/> when the loop may claim.</returns>
    internal static async Task<bool> OpenAsync(
        AlvoEventOptions events, AlvoBootState boot, IPolicyCatalogProvider catalogs, Action logManual, Action logUnprimed, CancellationToken stop)
    {
        if (!events.Enabled)
        {
            return false;
        }

        if (events.DispatchMode is AlvoEventDispatchMode.Manual)
        {
            logManual();
            return false;
        }

        if (await boot.SettledAsync(stop).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return false;
        }

        if (catalogs.Current is null)
        {
            logUnprimed();
            return false;
        }

        return true;
    }
}
