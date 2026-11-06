using MMLib.Alvo.Events;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The steps half of the store: memoized step results over <see cref="StepTable"/>.</summary>
internal sealed partial class EfCoreDeliveryStore
{
    public async Task<IReadOnlyList<DeliveryStep>> StepsAsync(Guid eventId, string subscriber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);

        return await ReadAsync(
            StepTable.ReadSql(_steps),
            command => BindStepKey(command, eventId, subscriber),
            reader => ReadStep(reader, eventId, subscriber),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<DeliveryStep> RecordStepAsync(DeliveryStep completed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentException.ThrowIfNullOrWhiteSpace(completed.Subscriber);
        ArgumentException.ThrowIfNullOrEmpty(completed.Name);
        ArgumentNullException.ThrowIfNull(completed.Result);

        return InTransactionAsync((connection, transaction) => RecordStepInAsync(connection, transaction, completed, cancellationToken), cancellationToken);
    }

    public Task<bool> SleepAsync(DeliveryAttempt attempt, DateTimeOffset wakeAt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Task 3");

    private async Task<DeliveryStep> RecordStepInAsync(DbConnection connection, DbTransaction transaction, DeliveryStep completed, CancellationToken ct)
    {
        await ExecuteInAsync(connection, transaction, StepTable.RecordSql(_steps), command => BindStep(command, completed), ct).ConfigureAwait(false);
        var stored = await RelationalSqlBatch.ReadRowsAsync(
            connection,
            transaction,
            StepTable.ReadOneSql(_steps),
            command => BindStepName(command, completed),
            reader => completed with { Result = reader.GetString(0), CompletedAt = StoredInstant.Of(reader.GetString(1)) },
            ct).ConfigureAwait(false);

        return stored.Single();
    }

    private static void BindStep(DbCommand command, DeliveryStep completed)
    {
        BindStepName(command, completed);
        RelationalSqlBatch.AddParameter(command, "@result", completed.Result);
        RelationalSqlBatch.AddParameter(command, "@completed_at", StoredInstant.Text(completed.CompletedAt));
    }

    private static void BindStepName(DbCommand command, DeliveryStep completed)
    {
        BindStepKey(command, completed.EventId, completed.Subscriber);
        RelationalSqlBatch.AddParameter(command, "@step", completed.Name);
    }

    private static void BindStepKey(DbCommand command, Guid eventId, string subscriber)
    {
        RelationalSqlBatch.AddParameter(command, "@event_id", eventId.ToString());
        RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
    }

    private static DeliveryStep ReadStep(DbDataReader reader, Guid eventId, string subscriber) => new()
    {
        EventId = eventId,
        Subscriber = subscriber,
        Name = reader.GetString(0),
        Result = reader.GetString(1),
        CompletedAt = StoredInstant.Of(reader.GetString(2)),
    };
}
