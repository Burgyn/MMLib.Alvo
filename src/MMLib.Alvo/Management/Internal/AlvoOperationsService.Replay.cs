using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Globalization;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// A replay: a dry run counts, a real run resets under an optional idempotency key whose record is the operations log row
/// the store writes in the reset's own transaction. With it the service is the whole <see cref="IAlvoOperations"/>.
/// </summary>
/// <remarks>
/// A real run always writes its log row and its one audit line, even when it reset nothing (plan Ruling 5): the row is its
/// key's record. A retry answered from that record writes neither.
/// <para>
/// <b>What makes a retry the same request</b> is normalized before it is hashed: a missing status is the <c>dead</c> it
/// defaults to — and the filter uses the same value, so the hash never describes a different selection than the reset — and
/// the window's instants are hashed in UTC, so one instant spelled in two offsets is one request.
/// </para>
/// <para>
/// <b>A duplicate that races the first run is answered by the record only once that run has committed.</b> A duplicate whose
/// key lookup and selection run while the first run's transaction is still open sees no record, and its selection may be
/// refused — 409 when a selected record has a delivery running, or 422 past <see cref="MaxReplay"/> — before its own
/// reset would have found the key recorded. That is the window plan Ruling 7 accepts for the count check, stated rather
/// than locked against; a retry after the first answer arrives always reads the record.
/// </para>
/// </remarks>
internal sealed partial class AlvoOperationsService : IAlvoOperations
{
    internal const int MaxReplay = 10_000;

    private const string ReplayOperation = "replay";

    private const string DefaultReplayStatus = "dead";

    private const string KeyOnDryRun =
        "A dry run may not carry an 'Idempotency-Key'. It resets nothing, so there is nothing to record — and a record filed "
        + "here would turn your real replay into a replay of a preview. Send the key with the real replay.";

    private const string KeySpentElsewhere =
        "This 'Idempotency-Key' was already spent on a different replay. Send a fresh key with this body, or resend the original body to read the answer it recorded.";

    public async Task<ManagementReplayPlan> ReplayAsync(string project, ManagementReplayRequest request, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.ReplayDeliveries, project);
        var filter = ReplayFilter(request);
        var token = ReplayToken(project, request);
        if (request.DryRun)
        {
            return await PlanAsync(filter, ct).ConfigureAwait(false);
        }

        return await ReplayedAsync(token, ct).ConfigureAwait(false)
            ?? await ResetAsync(filter, request, token, ct).ConfigureAwait(false);
    }

    internal static string ReplayFingerprint(string project, ManagementReplayRequest request) =>
        ManagementIdempotency.FingerprintOf(
            nameof(ReplayAsync),
            project,
            expectedRevision: 0,
            allowDestructive: false,
            string.Join('\n', request.Subscriber, StatusOf(request), Instant(request.From), Instant(request.To)));

    private static string Instant(DateTimeOffset? instant) => instant?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string StatusOf(ManagementReplayRequest request) => request.Status ?? DefaultReplayStatus;

    private DeliveryFilter ReplayFilter(ManagementReplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationReasons.Ensure(request.Reason);
        Handler(request.Subscriber);
        DeliveryQueries.EnsureStatus(StatusOf(request), DeliveryQueries.Finished, "replayed");

        return DeliveryQueries.Window(request.From, request.To) with
        {
            Subscriber = request.Subscriber,
            Status = StatusOf(request),
            Replayable = true,
            Limit = SampleSize,
        };
    }

    private ManagementIdempotencyToken? ReplayToken(string project, ManagementReplayRequest request)
    {
        if (request.IdempotencyKey is not { } key)
        {
            return null;
        }

        if (request.DryRun)
        {
            throw new ManagementRequestException(KeyOnDryRun);
        }

        var caller = gate.Caller;
        try
        {
            AlvoIdempotency.EnsureUsableKey(key, caller);
        }
        catch (ArgumentException refusal)
        {
            throw new ManagementRequestException(refusal.Message, refusal);
        }

        return new ManagementIdempotencyToken(key, AlvoIdempotency.IdentityOf(caller), ReplayFingerprint(project, request));
    }

    private async Task<ManagementReplayPlan> PlanAsync(DeliveryFilter filter, CancellationToken ct)
    {
        var selection = await Store.SelectAsync(filter, ct).ConfigureAwait(false);

        return new ManagementReplayPlan { DryRun = true, Count = selection.Count, Ids = selection.Sample };
    }

    private async Task<ManagementReplayPlan?> ReplayedAsync(ManagementIdempotencyToken? token, CancellationToken ct)
    {
        if (token is not { } spent || await Store.FindOperationAsync(spent.Scope, spent.Key, ct).ConfigureAwait(false) is not { } recorded)
        {
            return null;
        }

        return string.Equals(recorded.Fingerprint, spent.Fingerprint, StringComparison.Ordinal)
            ? new ManagementReplayPlan { DryRun = false, Count = recorded.Affected ?? 0, Ids = recorded.Sample, Replayed = true }
            : throw new AlvoIdempotencyConflictException(KeySpentElsewhere);
    }

    private async Task<ManagementReplayPlan> ResetAsync(DeliveryFilter filter, ManagementReplayRequest request, ManagementIdempotencyToken? token, CancellationToken ct)
    {
        EnsureResettable(await Store.SelectAsync(filter, ct).ConfigureAwait(false), request.Subscriber);
        var entry = Entry(ReplayOperation, request.Subscriber, request.Reason) with
        {
            IdempotencyScope = token?.Scope,
            IdempotencyKey = token?.Key,
            Fingerprint = token?.Fingerprint,
        };
        if (await Store.ReplayAsync(filter, entry, ct).ConfigureAwait(false) is not { } recorded)
        {
            return await ReplayedAsync(token, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The delivery store refused the replay's key and then did not find it under that key — a store that breaks IDeliveryStore's contract.");
        }

        OperationsAudit.Write(logger, recorded);

        return new ManagementReplayPlan { DryRun = false, Count = recorded.Affected ?? 0, Ids = recorded.Sample };
    }

    private static void EnsureResettable(DeliverySelection selected, string subscriber)
    {
        if (selected.Count > MaxReplay)
        {
            throw new ManagementRequestException(
                $"The replay selects {selected.Count} deliveries; one replay resets at most {MaxReplay}. Narrow 'from' and 'to', and replay the window in parts.");
        }

        if (selected.Busy > 0)
        {
            throw new ManagementConflictException(
                $"{selected.Busy} of the {selected.Count} deliveries belong to a record whose delivery for '{subscriber}' is running now, "
                + "so replaying them would run two of one record at once. Wait for it to be answered, then replay again.");
        }
    }
}
