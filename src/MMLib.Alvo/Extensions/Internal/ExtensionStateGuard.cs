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
/// <b>Two steps of the boot, not a service of its own.</b> <c>AlvoBootService</c> runs <see cref="RefuseOrphansAsync"/>
/// as soon as stage 1 has brought the system tables up — the delivery tables among them — and before anything is
/// converged, on every boot shape that is going to reach Ready; it runs <see cref="RetireAsync"/> last, just before it
/// publishes Ready. Nothing waits on another service, so no host shape can hang on either, and a host whose boot never
/// runs never runs them.
/// </para>
/// <para>
/// <b>A refused start changes nothing.</b> The check only reads, and it comes first: a build that retires one id and
/// forgets another must not end the first id's work and then refuse, or the rollback that follows a refused deploy would
/// find its handler's lanes already ended by a build that never served. Its refusal stops the process on every shape,
/// because the fix is a code change — register the id again, or retire it — which the running process cannot take.
/// </para>
/// </remarks>
internal sealed class ExtensionStateGuard(ExtensionRegistry registry, IDeliveryStore store, ILogger<ExtensionStateGuard> logger)
{
    /// <summary>Refuses the start when unfinished work belongs to an id this host neither registers nor retires; reads only.</summary>
    /// <param name="cancellationToken">Cancels the check, failing the boot.</param>
    /// <exception cref="AlvoStartupRefusedException">Unfinished work belongs to an id neither registered nor retired.</exception>
    internal async Task RefuseOrphansAsync(CancellationToken cancellationToken)
    {
        var known = registry.All.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = (await store.PendingSubscribersAsync(cancellationToken).ConfigureAwait(false))
            .Where(id => !known.Contains(id)).ToList();
        if (orphans.Count > 0)
        {
            throw Orphaned(orphans);
        }
    }

    /// <summary>Applies every <c>AddRetiredExtension</c>: dead-letters or cancels the id's unfinished deliveries.</summary>
    /// <param name="cancellationToken">Cancels the step, failing the boot.</param>
    internal async Task RetireAsync(CancellationToken cancellationToken)
    {
        foreach (var retired in registry.Retired)
        {
            var ended = retired.Retirement is AlvoRetirement.Discard
                ? await store.CancelPendingAsync(retired.Id, cancellationToken).ConfigureAwait(false)
                : await store.DeadLetterPendingAsync(retired.Id, DeliveryErrors.Retired, cancellationToken).ConfigureAwait(false);
            ExtensionLog.ExtensionRetired(logger, retired.Id, retired.Retirement.ToString(), ended);
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
