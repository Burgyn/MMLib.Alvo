using MMLib.Alvo.Events;

using System.Text.Json;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The operator's reads that leave the store: a redacted fixture, and this instance's stats.</summary>
/// <remarks>
/// <b>Deviation from the plan's Task 12 sketch</b>, which passed the stored envelope to <see cref="DeliveryFixtures"/>
/// unguarded: an envelope that is not a JSON object is refused there with a <see cref="JsonException"/>, which is answered
/// here as a conflict with a fixed message and no inner exception, so no part of the stored payload reaches the caller.
/// </remarks>
internal sealed partial class AlvoOperationsService
{
    public async Task<DeliveryFixture> GetFixtureAsync(string project, Guid delivery, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetDeliveryFixture, project);
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);

        return row.Payload is { } payload
            ? FixtureOf(row, payload)
            : throw new ManagementConflictException(
                $"The outbox no longer holds the event of delivery '{delivery}', so there is no envelope to make a fixture of.");
    }

    public Task<ManagementExtensionStats> GetExtensionStatsAsync(string project, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetExtensionStats, project);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(stats.Report([.. catalog.Items.Select(item => item.Id)]));
    }

    private DeliveryFixture FixtureOf(StoredDelivery row, string payload)
    {
        try
        {
            return DeliveryFixtures.Of(row, payload, schemas.GetSchema());
        }
        catch (JsonException)
        {
            throw new ManagementConflictException(
                $"The stored envelope of delivery '{row.Id}' is not a JSON object, so no fixture can be taken from it.");
        }
    }
}
