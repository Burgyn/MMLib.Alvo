using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Scheduling;

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
    private const string DisableOperation = "disable-extension";
    private const string EnableOperation = "enable-extension";
    private const string ChainDepthExceeded = "chain-depth-exceeded";
    private const string RunScheduleOperation = "run-schedule";
    private const int RunPage = 50;

    private const string NoTimers =
        "No timer store is registered, so no schedule can be run. Register a relational data provider — UseSqlite or "
        + "UsePostgreSql — which keeps schedules beside the outbox.";

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

    public async Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.SetExtensionEnabled, project);
        OperationReasons.Ensure(reason);
        var found = Registered(extension);
        EnsureSwitchable(found, project, enabled);
        var entry = Entry(enabled ? EnableOperation : DisableOperation, found.Id, reason);
        if (await Store.SetDisabledAsync(found.Id, !enabled, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }

        return found with { Enabled = enabled };
    }

    /// <summary>Only a validate hook is switched (Rulings 5, 51): a handler is paused instead, and an in-transaction hook guards integrity.</summary>
    private static void EnsureSwitchable(ManagementExtension found, string project, bool enabled)
    {
        if (found.Kind == AlvoExtensionKind.Validate)
        {
            return;
        }

        var only = $"only a validate hook can be switched {(enabled ? "on" : "off")}";
        throw new ManagementConflictException(found.Kind switch
        {
            AlvoExtensionKind.EventHandler =>
                $"'{found.Id}' is an event handler, and {only}. To stop a handler for a while, "
                + $"pause it instead: PUT {{m}}/projects/{project}/subscribers/{found.Id}/paused with {{\"paused\": true}}.",
            AlvoExtensionKind.Command =>
                $"'{found.Id}' is a command, and {only}: a command is changed in the host's code and redeployed.",
            AlvoExtensionKind.Schedule =>
                $"'{found.Id}' is a schedule, and {only}: a schedule is changed or retired in the host's code and redeployed; to "
                + $"run it outside its cron, POST {{m}}/projects/{project}/schedules/{found.Id}/run.",
            _ =>
                $"'{found.Id}' is a {ExtensionCatalog.WireName(found.Kind)} extension, and {only}: a hook inside the write's transaction "
                + "guards the data's integrity, so it is changed in the host's code and redeployed.",
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The run's events are appended and fanned out at once — the dispatcher's later fan-out of the same events adds nothing —
    /// so the answer is a delivery that exists: for a per-tenant schedule, the one of the first tenant's event, found by its
    /// event id rather than as the newest row, since every later tenant's delivery is newer.
    /// </remarks>
    public async Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.RunSchedule, project);
        var job = catalog.Schedule(schedule)
            ?? throw new ManagementExtensionNotFoundException($"This host registers no schedule '{schedule}'. Schedules are registered in the host with AddSchedule.");
        var events = await (runs ?? throw new ManagementRequestException(NoTimers)).EventsAsync(job, time.GetUtcNow(), manual: true, ct).ConfigureAwait(false);
        if (events.Count == 0)
        {
            throw new ManagementConflictException($"'{schedule}' runs once per tenant, and the host's IAlvoTenantCatalog lists no tenant, so there is nothing to run.");
        }

        await StartRunAsync(schedule, events, ct).ConfigureAwait(false);

        return await RunDeliveryAsync(schedule, events[0].Envelope.Id, ct).ConfigureAwait(false);
    }

    private async Task StartRunAsync(string schedule, IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken ct)
    {
        await (timers ?? throw new ManagementRequestException(NoTimers)).AppendRunAsync(events, ct).ConfigureAwait(false);
        await Store.FanOutAsync([.. events.Select(framework => RunRequest(framework.Envelope, schedule))], ct).ConfigureAwait(false);
        OperationsAudit.Write(logger, Entry(RunScheduleOperation, schedule, reason: null) with { Affected = events.Count });
    }

    private static DeliveryRequest RunRequest(AlvoEvent run, string schedule) => new()
    {
        EventId = run.Id,
        Subscriber = schedule,
        PartitionKey = run.PartitionKey,
        TenantId = run.TenantId,
        Kind = DeliveryKind.Schedule,
    };

    /// <summary>The delivery of <paramref name="eventId"/> for <paramref name="schedule"/>, paged by event id from that event on.</summary>
    private async Task<ManagementDelivery> RunDeliveryAsync(string schedule, Guid eventId, CancellationToken ct)
    {
        Guid? after = null;
        while (true)
        {
            var page = await Store.ListAsync(new DeliveryFilter { Subscriber = schedule, FromEventId = eventId, After = after, Limit = RunPage }, ct).ConfigureAwait(false);
            if (page.FirstOrDefault(row => row.EventId == eventId) is { } row)
            {
                return await DetailAsync(row.Id, ct).ConfigureAwait(false);
            }

            after = page.Count == RunPage ? page[^1].Id : throw new InvalidOperationException(
                $"The delivery of '{schedule}' for event '{eventId}' was just fanned out, and the store does not list it.");
        }
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
