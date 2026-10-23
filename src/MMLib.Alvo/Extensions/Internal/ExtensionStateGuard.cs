using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// At start-up, decides what becomes of durable work keyed by an id this host no longer registers (spec 2026-10-14 E3):
/// a declared retirement dead-letters or discards it; anything else refuses the start, because that work would never run
/// and, with per-record order, the lanes behind it would wait forever.
/// </summary>
/// <remarks>
/// <para>
/// Registered after the boot service, and awaiting its settlement when the host starts services concurrently, so it runs
/// against the primed, migrated database, and
/// in <see cref="StartingAsync"/>, so it finishes before any <see cref="IHostedService.StartAsync"/> — the delivery
/// dispatcher cannot claim work a retirement is about to end.
/// </para>
/// <para>
/// <b>The same on every boot shape that reaches ready</b> — a file descriptor, a stored one, or a stored-descriptor host
/// still awaiting its first: the fix is a code change (register the id again, or retire it), which the running process
/// cannot take, so the refusal throws like <c>ExtensionBinder.EnsureRegistrable</c> rather than recording a 503 the way
/// a descriptor a dashboard can replace does. The refusal is published on <see cref="AlvoBootState"/> before the throw. A
/// boot that refused has already said why, and a host with no boot at all serves nothing; in neither does anything
/// deliver, so the guard checks nothing.
/// </para>
/// </remarks>
internal sealed class ExtensionStateGuard(
    AlvoBootState boot,
    ExtensionRegistry registry,
    IDeliveryStore store,
    IOptions<HostOptions> host,
    ILogger<ExtensionStateGuard> logger)
    : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (await BootPhaseAsync(cancellationToken).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return;
        }

        await store.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await RetireAsync(cancellationToken).ConfigureAwait(false);
        await RefuseOrphansAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The phase the boot settled on: awaited when the host starts its services concurrently, read when it starts them in
    /// order — the boot's <see cref="IHostedLifecycleService.StartingAsync"/> has then already returned, and a phase still
    /// pending means no boot ran in this host, which waiting would turn into a start that never finishes.
    /// </summary>
    private async Task<AlvoBootPhase> BootPhaseAsync(CancellationToken ct) =>
        host.Value.ServicesStartConcurrently ? await boot.SettledAsync(ct).ConfigureAwait(false) : boot.Phase;

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
            var refusal = Orphaned(orphans);
            boot.Failed(refusal.Message);
            throw refusal;
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
