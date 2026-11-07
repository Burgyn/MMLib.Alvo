using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Ends the durable work of descriptor actions the serving catalog no longer binds (issue 480): an edited or removed action,
/// condition or rule trigger makes a new subscriber id (<see cref="DescriptorActionIds"/>), and nothing ever claims the old
/// one's unfinished deliveries again, nor runs its cron schedule. Run after each committed apply or rollback and at the boot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only descriptor actions.</b> An id that does not start with <c>/</c> is a host registration — its retirement is the
/// host's to declare (<c>AddRetiredExtension</c>), and the boot refuses it otherwise — so the sweep never touches one.
/// </para>
/// <para>
/// <b>Per subscriber, across tenants.</b> A subscriber id names one action of one catalog whatever the event's tenant, and
/// the store ends work by subscriber, so the sweep cannot reach another subscriber's rows — a live action's included.
/// </para>
/// <para>
/// <b>Each cancelled subscriber is one operations-log row</b> (<see cref="Operation"/>, by <see cref="Actor"/>, naming the
/// revision), written in the cancel's own transaction by a store that implements <see cref="ISupersededDeliveryStore"/>; a
/// store that does not still has its work cancelled, through <see cref="IDeliveryStore.CancelPendingAsync"/>, and the audit
/// log line is all that records it. A subscriber with nothing unfinished records nothing, so a second sweep — another
/// replica's, or the next apply's — is a no-op.
/// </para>
/// <para>
/// <b>Cancelled is final.</b> A rollback that binds the old id again does not bring its cancelled rows back: the restored
/// action receives the events written from then on, and an operator who wants the old ones run redelivers or replays them.
/// </para>
/// <para>
/// <b>A cron rule's stored schedule</b> whose trigger changed or whose rule is gone is deleted through
/// <see cref="ITimerStore.SyncSchedulesAsync"/>'s retired ids; it would never be claimed again either.
/// </para>
/// <para>
/// <b>It never fails the apply.</b> It runs after the commit; a failure is logged, and the next apply or boot sweeps again.
/// </para>
/// </remarks>
/// <param name="binder">Binds the catalog; its handlers are what the catalog binds.</param>
/// <param name="time">The clock the log rows are stamped with.</param>
/// <param name="logger">Where each cancelled subscriber's audit line and a failed sweep are written.</param>
/// <param name="deliveries">The delivery store, or <see langword="null"/> when no provider registered one.</param>
/// <param name="timers">The timer store holding cron schedules, or <see langword="null"/> when no provider registered one.</param>
internal sealed partial class DescriptorActionSweep(
    ExtensionBinder binder,
    TimeProvider time,
    ILogger<DescriptorActionSweep> logger,
    IDeliveryStore? deliveries = null,
    ITimerStore? timers = null)
{
    /// <summary>The operations-log operation of one cancelled subscriber.</summary>
    internal const string Operation = "cancel-superseded";

    /// <summary>Who the operations log names: the framework, not an operator.</summary>
    internal const string Actor = "system";

    private int _ensured;

    /// <summary>Ends the work of every descriptor action <paramref name="catalog"/> does not bind; never throws but for cancellation.</summary>
    /// <param name="catalog">The catalog now serving.</param>
    /// <param name="revision">The revision it serves, named by each log row; <see langword="null"/> when unknown.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    internal async Task SweepAsync(PolicyCatalog catalog, int? revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            await EnsureStoresAsync(cancellationToken).ConfigureAwait(false);
            var bound = binder.Result(catalog).Bindings.Handlers.Select(handler => handler.Id).ToHashSet(StringComparer.Ordinal);
            await CancelDeliveriesAsync(bound, revision, cancellationToken).ConfigureAwait(false);
            await RemoveSchedulesAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            SweepFailed(logger, failure.GetType().Name);
        }
    }

    private async Task EnsureStoresAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _ensured) == 1)
        {
            return;
        }

        await (deliveries?.EnsureAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
        await (timers?.EnsureAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
        Volatile.Write(ref _ensured, 1);
    }

    private async Task CancelDeliveriesAsync(HashSet<string> bound, int? revision, CancellationToken ct)
    {
        if (deliveries is null)
        {
            return;
        }

        var stale = (await deliveries.PendingSubscribersAsync(ct).ConfigureAwait(false)).Where(id => IsStale(id, bound));
        foreach (var subscriber in stale.Order(StringComparer.Ordinal))
        {
            await CancelAsync(deliveries, subscriber, Entry(subscriber, revision), ct).ConfigureAwait(false);
        }
    }

    private async Task CancelAsync(IDeliveryStore store, string subscriber, DeliveryOperationEntry entry, CancellationToken ct)
    {
        var cancelled = store is ISupersededDeliveryStore superseded
            ? await superseded.CancelSupersededAsync(subscriber, entry, ct).ConfigureAwait(false)
            : await store.CancelPendingAsync(subscriber, ct).ConfigureAwait(false);
        if (cancelled > 0)
        {
            OperationsAudit.Write(logger, entry with { Affected = cancelled });
        }
    }

    private async Task RemoveSchedulesAsync(HashSet<string> bound, CancellationToken ct)
    {
        if (timers is null)
        {
            return;
        }

        List<string> stale = [.. (await timers.SchedulesAsync(ct).ConfigureAwait(false)).Select(schedule => schedule.Id).Where(id => IsStale(id, bound))];
        if (stale.Count > 0)
        {
            await timers.SyncSchedulesAsync([], stale, ct).ConfigureAwait(false);
            SchedulesRemoved(logger, string.Join(", ", stale));
        }
    }

    private static bool IsStale(string id, HashSet<string> bound) => DescriptorActionIds.IsDescriptorAction(id) && !bound.Contains(id);

    private DeliveryOperationEntry Entry(string subscriber, int? revision)
    {
        var now = time.GetUtcNow();

        return new DeliveryOperationEntry
        {
            Id = AlvoEventId.Create(now),
            At = now,
            Actor = Actor,
            Operation = Operation,
            Target = subscriber,
            Reason = revision is { } served
                ? $"Revision {served} no longer declares this action; its unfinished deliveries were cancelled."
                : "The serving descriptor no longer declares this action; its unfinished deliveries were cancelled.",
        };
    }

    [LoggerMessage(
        EventName = "DescriptorActionSweepFailed",
        Level = LogLevel.Warning,
        Message = "Alvo could not end the work of descriptor actions the serving descriptor no longer declares ({Failure}). "
            + "Their unfinished deliveries stay listed until the next apply or boot sweeps again.")]
    private static partial void SweepFailed(ILogger logger, string failure);

    [LoggerMessage(
        EventName = "DescriptorActionSchedulesRemoved",
        Level = LogLevel.Information,
        Message = "Alvo removed the stored schedules of cron rules the serving descriptor no longer declares: {Ids}.")]
    private static partial void SchedulesRemoved(ILogger logger, string ids);
}
