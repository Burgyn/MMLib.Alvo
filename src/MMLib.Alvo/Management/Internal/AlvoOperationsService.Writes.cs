using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The operator's changes: each audited once when it changed something, none when it did not.</summary>
/// <remarks>
/// <b>Deviation from the plan's Task 13 sketch</b>, which answered a delivery the store declined to reset as unchanged:
/// the row is read again. Still finished, the store declined because its lane has a delivery running — the same conflict
/// the read-side guard raises, so a lane that started running between the guard and the reset is refused, not silently
/// reported as done. No longer finished, another redeliver landed first, and the delivery is answered as it is now with
/// no audit, as any redeliver that changed nothing is.
/// </remarks>
internal sealed partial class AlvoOperationsService
{
    private const string RedeliverOperation = "redeliver";
    private const string PauseOperation = "pause";
    private const string ResumeOperation = "resume";
    private const string ChainDepthExceeded = "chain-depth-exceeded";

    public async Task<ManagementDelivery> RedeliverAsync(string project, Guid delivery, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.RedeliverDelivery, project);
        OperationReasons.Ensure(reason);
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);
        await EnsureRedeliverableAsync(row, ct).ConfigureAwait(false);
        if (DeliveryQueries.Finished.Contains(row.Status))
        {
            await ResetAsync(row, reason, ct).ConfigureAwait(false);
        }

        return await DetailAsync(delivery, ct).ConfigureAwait(false);
    }

    public async Task<ManagementExtension> SetPausedAsync(string project, string subscriber, bool paused, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.PauseSubscriber, project);
        OperationReasons.Ensure(reason);
        var handler = Handler(subscriber);
        var entry = Entry(paused ? PauseOperation : ResumeOperation, subscriber, reason);
        if (await Store.SetPausedAsync(subscriber, paused, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }

        return handler with { Paused = paused };
    }

    public Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.SetExtensionEnabled, project);
        OperationReasons.Ensure(reason);
        ct.ThrowIfCancellationRequested();
        var found = Registered(extension);

        throw new ManagementConflictException(
            $"'{found.Id}' is an event handler, and only a validate hook can be switched {(enabled ? "on" : "off")}. To stop a handler for a while, "
            + $"pause it instead: PUT {{m}}/projects/{project}/subscribers/{found.Id}/paused with {{\"paused\": true}}.");
    }

    public Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.RunSchedule, project);
        ct.ThrowIfCancellationRequested();

        throw new ManagementExtensionNotFoundException(
            $"This host registers no schedule '{schedule}'. Schedules are registered in the host with AddSchedule; it has none.");
    }

    private async Task ResetAsync(StoredDelivery row, string? reason, CancellationToken ct)
    {
        var entry = Entry(RedeliverOperation, row.Id.ToString(), reason);
        if (await Store.RedeliverAsync(row.Id, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }
        else if (DeliveryQueries.Finished.Contains((await FoundAsync(row.Id, ct).ConfigureAwait(false)).Status))
        {
            throw LaneRunning(row);
        }
    }

    private void EnsureRedeliverable(StoredDelivery row)
    {
        if (!catalog.Items.Any(item => item.Id == row.Subscriber))
        {
            throw new ManagementConflictException(
                $"Delivery '{row.Id}' is for '{row.Subscriber}', which this host does not register, so a redelivered delivery would never "
                + "run — and would stop the next start as unknown work. Register the handler again, or retire it with AddRetiredExtension.");
        }

        if (row.Status == "running")
        {
            throw new ManagementConflictException(
                $"Delivery '{row.Id}' is running under a claim. Wait for its attempt to be answered, then redeliver it if it ends dead.");
        }

        if (row.Status == "dead" && row.LastErrorType == ChainDepthExceeded)
        {
            throw new ManagementConflictException(
                $"Delivery '{row.Id}' is dead with {ChainDepthExceeded}: the chain-depth limit stopped a loop there, and running it again "
                + "would restart the loop. Fix the handler that writes what it reacts to; events that should still run can then be replayed.");
        }
    }

    private async Task EnsureRedeliverableAsync(StoredDelivery row, CancellationToken ct)
    {
        EnsureRedeliverable(row);
        if (DeliveryQueries.Finished.Contains(row.Status) && await LaneRunsAsync(row, ct).ConfigureAwait(false))
        {
            throw LaneRunning(row);
        }
    }

    private static ManagementConflictException LaneRunning(StoredDelivery row) => new(
        $"Another delivery of '{row.PartitionKey}' for '{row.Subscriber}' is running, so a redelivered one would run beside it. "
        + "Wait for it to be answered, then redeliver.");

    private async Task<bool> LaneRunsAsync(StoredDelivery row, CancellationToken ct) =>
        (await Store.ListAsync(
            new DeliveryFilter { Subscriber = row.Subscriber, PartitionKey = row.PartitionKey, Status = "running", Limit = 1 }, ct)
            .ConfigureAwait(false)).Count > 0;

    private ManagementExtension Handler(string subscriber) =>
        catalog.Items.FirstOrDefault(item => item.Id == subscriber && item.Kind == AlvoExtensionKind.EventHandler)
        ?? throw new ManagementExtensionNotFoundException(subscriber, [.. catalog.Items.Select(item => item.Id)]);

    private ManagementExtension Registered(string extension) =>
        catalog.Items.FirstOrDefault(item => item.Id == extension)
        ?? throw new ManagementExtensionNotFoundException(extension, [.. catalog.Items.Select(item => item.Id)]);

    private DeliveryOperationEntry Entry(string operation, string target, string? reason)
    {
        var now = time.GetUtcNow();

        return new DeliveryOperationEntry
        {
            Id = AlvoEventId.Create(now),
            At = now,
            Actor = gate.Actor,
            Operation = operation,
            Target = target,
            Reason = reason,
        };
    }
}
