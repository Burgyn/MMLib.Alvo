using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The operator's port over handler deliveries: reads here and in <c>AlvoOperationsService.Reads.cs</c>, changes in
/// <c>AlvoOperationsService.Writes.cs</c> and <c>AlvoOperationsService.Replay.cs</c>. Every member is admitted by
/// <see cref="ManagementGate"/> first, exactly as the management surface's members are.
/// </summary>
/// <remarks>
/// <b>The project names admission, not a partition.</b> <see cref="IDeliveryStore"/> is the host's one store over its one
/// database, and a delivery row carries no project: the gate refuses a project this instance does not serve, and what an
/// admitted caller then reads is that database's deliveries — every project this host boots over it shares them, as they
/// share the outbox the deliveries were fanned out from.
/// </remarks>
internal sealed partial class AlvoOperationsService(
    ManagementGate gate,
    ExtensionCatalog catalog,
    ExtensionStats stats,
    ISchemaRegistry schemas,
    TimeProvider time,
    ILogger<AlvoOperationsService> logger,
    IDeliveryStore? store = null)
{
    internal const int SampleSize = 20;

    private const string NoStore =
        "No delivery store is registered, so there are no deliveries to operate on. Register a data provider — UseSqlite or "
        + "UsePostgreSql — which brings the delivery store with it.";

    public async Task<ManagementDeliveryPage> ListDeliveriesAsync(string project, ManagementDeliveryQuery query, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.ListDeliveries, project);
        var filter = DeliveryQueries.Filter(query);
        var rows = await Store.ListAsync(filter, ct).ConfigureAwait(false);

        return new ManagementDeliveryPage { Items = [.. rows.Select(DeliveryViews.Of)], Next = rows.Count == filter.Limit ? rows[^1].Id : null };
    }

    public async Task<ManagementDelivery> GetDeliveryAsync(string project, Guid delivery, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetDelivery, project);

        return await DetailAsync(delivery, ct).ConfigureAwait(false);
    }

    private IDeliveryStore Store => store ?? throw new ManagementRequestException(NoStore);

    private async Task<ManagementDelivery> DetailAsync(Guid delivery, CancellationToken ct)
    {
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);
        var attempts = await Store.AttemptsAsync(delivery, ct).ConfigureAwait(false);

        return DeliveryViews.Of(row) with { History = [.. attempts.Select(DeliveryViews.Of)] };
    }

    private async Task<StoredDelivery> FoundAsync(Guid delivery, CancellationToken ct) =>
        await Store.FindAsync(delivery, ct).ConfigureAwait(false) ?? throw new ManagementDeliveryNotFoundException(delivery);
}
