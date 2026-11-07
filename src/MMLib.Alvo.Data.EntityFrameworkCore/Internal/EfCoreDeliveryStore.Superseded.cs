using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The end of a superseded descriptor action's work (issue 480), over <see cref="DeliveryTable.EndPendingSql"/>.</summary>
internal sealed partial class EfCoreDeliveryStore : ISupersededDeliveryStore
{
    /// <summary>
    /// The subscriber's lock exclusively first — so a claim of it under way finishes before, and none starts until this
    /// commits — then the end of its unfinished deliveries, then the log row when any ended.
    /// </summary>
    public Task<int> CancelSupersededAsync(string subscriber, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        EnsureEntry(entry);

        return InTransactionAsync(async (connection, transaction) =>
        {
            await LockSubscribersAsync(connection, transaction, [subscriber], exclusive: true, cancellationToken).ConfigureAwait(false);
            var cancelled = await ExecuteInAsync(connection, transaction, DeliveryTable.EndPendingSql(_table), command =>
            {
                RelationalSqlBatch.AddParameter(command, "@status", DeliveryTable.Cancelled);
                RelationalSqlBatch.AddParameter(command, "@reason", ISupersededDeliveryStore.Superseded);
                RelationalSqlBatch.AddParameter(command, "@now", Now);
                RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
            }, cancellationToken).ConfigureAwait(false);
            if (cancelled > 0)
            {
                await LogChangeAsync(connection, transaction, entry with { Affected = cancelled }, cancellationToken).ConfigureAwait(false);
            }

            return cancelled;
        }, cancellationToken);
    }
}
