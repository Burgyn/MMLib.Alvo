using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>What one load of the Operations page read: the handlers, their numbers, a page of deliveries, the open delivery.</summary>
/// <param name="Handlers">The host's event handlers.</param>
/// <param name="Stats">This instance's per-handler numbers.</param>
/// <param name="Deliveries">The first page of deliveries of the status asked for.</param>
/// <param name="Selected">The open delivery read again, or <see langword="null"/> when none was open.</param>
internal sealed record OperationsSnapshot(
    IReadOnlyList<ManagementExtension> Handlers,
    ManagementExtensionStats Stats,
    IReadOnlyList<ManagementDelivery> Deliveries,
    ManagementDelivery? Selected);

/// <summary>The Operations page's loads, latest wins.</summary>
/// <remarks>
/// <para>
/// <b>Every load takes a new generation, and only the newest one's answer is kept.</b> The status filters and Refresh are
/// not held back while a load runs, so a quick <c>dead</c> then <c>All</c> runs two loads at once; without the generation
/// the slower first one could land last and draw the dead-only list under the All filter. A superseded load answers
/// <see langword="null"/>, and its failure is dropped too — it would describe a question the operator no longer asks.
/// </para>
/// <para>
/// <b>Threading.</b> No <c>ConfigureAwait(false)</c>, as <c>ExpressionCheck</c>: the generation is read and written on the
/// circuit's context the page awaited on.
/// </para>
/// </remarks>
/// <param name="gateway">The operations gateway the page reads through.</param>
internal sealed class OperationsLoader(OperationsGateway gateway)
{
    private int _generation;

    /// <summary>Gets whether the newest load is still running.</summary>
    public bool Loading { get; private set; }

    /// <summary>Reads the page's state, or answers <see langword="null"/> when a newer load started meanwhile.</summary>
    /// <param name="status">The status filter, or <see langword="null"/> for all.</param>
    /// <param name="selected">The open delivery, read again with the rest, or <see langword="null"/>.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The snapshot, or <see langword="null"/> when this load was superseded.</returns>
    public async Task<OperationsSnapshot?> LoadAsync(string? status, Guid? selected, CancellationToken ct)
    {
        var generation = ++_generation;
        Loading = true;
        try
        {
            var snapshot = await ReadAsync(status, selected, ct);
            return IsNewest(generation) ? snapshot : null;
        }
        catch (Exception) when (!IsNewest(generation))
        {
            return null;
        }
        finally
        {
            Loading &= !IsNewest(generation);
        }
    }

    private bool IsNewest(int generation) => generation == _generation;

    private async Task<OperationsSnapshot> ReadAsync(string? status, Guid? selected, CancellationToken ct)
    {
        var handlers = await gateway.HandlersAsync(ct);
        var stats = await gateway.StatsAsync(ct);
        var deliveries = (await gateway.DeliveriesAsync(status, ct)).Items;
        var open = selected is { } id ? await gateway.DeliveryAsync(id, ct) : null;

        return new OperationsSnapshot(handlers, stats, deliveries, open);
    }
}
