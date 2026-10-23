using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Decides what becomes of durable work keyed by an id this host no longer registers (spec 2026-10-14 E3): a declared
/// retirement dead-letters or discards it; anything else refuses the start, because that work would never run and, with
/// per-record order, the lanes behind it would wait forever.
/// </summary>
/// <remarks>
/// <para>
/// <b>A step of the boot, not a service of its own.</b> <c>AlvoBootService</c> runs it after the schema is converged and
/// before it publishes Ready, on every boot shape that gets there — a file descriptor, a stored one, or a
/// stored-descriptor host still awaiting its first. Nothing waits on another service, so no host shape can hang on it,
/// and a host whose boot never runs never runs it. A boot that refused or stood down never reaches it either, and nothing
/// in that process delivers.
/// </para>
/// <para>
/// <b>Its refusal stops the process, on every shape.</b> The fix is a code change — register the id again, or retire it
/// — which the running process cannot take; the boot records it before the throw, as it does every code-level refusal.
/// </para>
/// </remarks>
internal sealed class ExtensionStateGuard(ExtensionRegistry registry, IDeliveryStore store, ILogger<ExtensionStateGuard> logger)
{
    /// <summary>Brings the delivery store up, applies every retirement, then refuses any work nobody owns.</summary>
    /// <param name="cancellationToken">Cancels the step, failing the boot.</param>
    /// <exception cref="AlvoStartupRefusedException">Unfinished work belongs to an id neither registered nor retired.</exception>
    internal async Task SettleAsync(CancellationToken cancellationToken)
    {
        await store.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await RetireAsync(cancellationToken).ConfigureAwait(false);
        await RefuseOrphansAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireAsync(CancellationToken ct)
    {
        foreach (var retired in registry.Retired)
        {
            var ended = retired.Retirement is AlvoRetirement.Discard
                ? await store.CancelPendingAsync(retired.Id, ct).ConfigureAwait(false)
                : await store.DeadLetterPendingAsync(retired.Id, DeliveryErrors.Retired, ct).ConfigureAwait(false);
            ExtensionLog.ExtensionRetired(logger, retired.Id, retired.Retirement.ToString(), ended);
        }
    }

    private async Task RefuseOrphansAsync(CancellationToken ct)
    {
        var known = registry.All.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = (await store.PendingSubscribersAsync(ct).ConfigureAwait(false)).Where(id => !known.Contains(id)).ToList();
        if (orphans.Count > 0)
        {
            throw Orphaned(orphans);
        }
    }

    private static AlvoStartupRefusedException Orphaned(IReadOnlyList<string> ids) => new(
        $"Alvo cannot start: the delivery store holds unfinished work for '{string.Join("', '", ids)}', which this host "
        + "neither registers nor retires. It would never run, and with per-record order the work queued behind it would "
        + "wait forever.",
        string.Join(Environment.NewLine, ids.Select(id =>
            $"Register '{id}' again, or declare what becomes of its work: AddRetiredExtension(\"{id}\", AlvoRetirement.DeadLetter) "
            + $"keeps it inspectable as dead; AddRetiredExtension(\"{id}\", AlvoRetirement.Discard) cancels it.")));
}
