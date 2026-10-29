using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The dashboard's one caller of <see cref="IAlvoOperations"/>: uncached — deliveries move under the operator's eyes — and
/// authorizing nothing; every call runs as the signed-in operator through <see cref="ManagementGateway.ForProjectAsync"/>.
/// </summary>
/// <param name="operations">The operations port, resolved in-process.</param>
/// <param name="management">The management gateway: the project, the operator, and the extension catalog's cache.</param>
internal sealed class OperationsGateway(IAlvoOperations operations, ManagementGateway management)
{
    private const int PageSize = 50;

    /// <summary>The host's event handlers, the catalog read again each time so a pause made elsewhere shows; a refusal is thrown.</summary>
    public async Task<IReadOnlyList<ManagementExtension>> HandlersAsync(CancellationToken ct) =>
        [.. (await management.ExtensionsAgainAsync(ct).ConfigureAwait(false)).Where(extension => extension.Kind == AlvoExtensionKind.EventHandler)];

    /// <summary>This instance's per-handler numbers over the last 24 hours.</summary>
    public Task<ManagementExtensionStats> StatsAsync(CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetExtensionStatsAsync(project, ct), ct);

    /// <summary>The first page of deliveries, optionally of one status.</summary>
    public Task<ManagementDeliveryPage> DeliveriesAsync(string? status, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.ListDeliveriesAsync(project, new ManagementDeliveryQuery { Status = status, Limit = PageSize }, ct), ct);

    /// <summary>One delivery with its attempt history.</summary>
    public Task<ManagementDelivery> DeliveryAsync(Guid delivery, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetDeliveryAsync(project, delivery, ct), ct);

    /// <summary>Runs one finished delivery again.</summary>
    public Task<ManagementDelivery> RedeliverAsync(Guid delivery, string? reason, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.RedeliverAsync(project, delivery, reason, ct), ct);

    /// <summary>The dry run of replaying a handler's dead deliveries: the count and a sample, changing nothing.</summary>
    public Task<ManagementReplayPlan> PlanReplayAsync(string subscriber, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.ReplayAsync(project, new ManagementReplayRequest { Subscriber = subscriber, DryRun = true }, ct), ct);

    /// <summary>Replays a handler's dead deliveries under <paramref name="key"/>, so a retried click resets nothing twice.</summary>
    public Task<ManagementReplayPlan> ReplayAsync(string subscriber, string? reason, string key, CancellationToken ct) =>
        management.ForProjectAsync(
            project => operations.ReplayAsync(project, new ManagementReplayRequest { Subscriber = subscriber, Reason = reason, IdempotencyKey = key }, ct),
            ct);

    /// <summary>A delivery's fixture, every non-key scalar redacted.</summary>
    public Task<DeliveryFixture> FixtureAsync(Guid delivery, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetFixtureAsync(project, delivery, ct), ct);

    /// <summary>Pauses or resumes a handler, then drops the cached catalog so the next read says which.</summary>
    public async Task<ManagementExtension> SetPausedAsync(string subscriber, bool paused, string? reason, CancellationToken ct)
    {
        var handler = await management.ForProjectAsync(project => operations.SetPausedAsync(project, subscriber, paused, reason, ct), ct).ConfigureAwait(false);
        management.Invalidate();

        return handler;
    }
}
