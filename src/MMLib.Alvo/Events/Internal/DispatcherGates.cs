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

    /// <summary>
    /// Refuses a manual round — of any step of <c>IAlvoEventPump.DrainAsync</c> — unless this process is driven by hand, with
    /// events on and a Ready boot that primed a catalog; one wording for every step, so a misconfigured drain is reported as
    /// the drain's, whichever step runs first.
    /// </summary>
    /// <exception cref="InvalidOperationException">A gate is shut.</exception>
    internal static void EnsureManual(AlvoEventOptions events, AlvoBootState boot, IPolicyCatalogProvider catalogs)
    {
        if (events.DispatchMode is not AlvoEventDispatchMode.Manual)
        {
            throw NotManual(events);
        }

        if (!events.Enabled)
        {
            throw EventsDisabled();
        }

        if (boot.Phase is not AlvoBootPhase.Ready)
        {
            throw NotReady();
        }

        if (catalogs.Current is null)
        {
            throw Unprimed();
        }
    }

    internal static InvalidOperationException NotManual(AlvoEventOptions configured) => new(
        "A manual dispatch round was refused before anything was claimed: this process drains its outbox with the "
        + "background loop, and two drivers of one outbox break per-entity-key ordering. Set "
        + $"{AlvoEventOptionsConfiguration.DispatchModeKey} to '{AlvoEventDispatchMode.Manual}' and "
        + $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} to true to drive it with "
        + $"IAlvoEventPump.DrainAsync. Configured: mode '{configured.DispatchMode}', enabled {configured.Enabled}.");

    internal static InvalidOperationException EventsDisabled() => new(
        "A manual dispatch round was refused before anything was claimed: the dispatch mode is manual, but events are "
        + $"switched off ({AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} = false), so there is "
        + $"nothing to drive. Set {AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} to true to drive "
        + "the outbox with IAlvoEventPump.DrainAsync.");

    internal static InvalidOperationException NotReady() => new(
        "A manual dispatch round was refused before anything was claimed: the boot has not reached Ready, and an event "
        + "judged against an unprimed catalog would match no hook and be retired as delivered. Start the host and let "
        + "its boot finish first.");

    internal static InvalidOperationException Unprimed() => new(
        "A manual dispatch round was refused before anything was claimed: the boot is Ready but primed no schema, "
        + "because the project has no descriptor applied yet, and an event judged against an unprimed catalog would "
        + "match no hook and be retired as delivered. Apply a descriptor to the project first; if it should already "
        + $"have one, check {AlvoSchemaOptions.ProjectEnvironmentVariable}.");
}
