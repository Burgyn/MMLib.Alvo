using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The schedule and data-migration half of the store, over <see cref="ScheduleTable"/> and <see cref="DataMigrationTable"/>:
/// builds no command of its own and goes through the main file's transaction, statement and read helpers.
/// </summary>
internal sealed partial class EfCoreTimerStore
{
    public Task SyncSchedulesAsync(
        IReadOnlyList<ScheduleDefinition> schedules, IReadOnlyList<string> retired, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(retired);

        return WriteAsync(
            async (connection, transaction, ct) =>
            {
                foreach (var schedule in schedules.OrderBy(schedule => schedule.Id, StringComparer.Ordinal))
                {
                    await ExecuteAsync(connection, transaction, ScheduleTable.SyncSql(_schedules), command => BindSchedule(command, schedule), ct)
                        .ConfigureAwait(false);
                }

                return retired.Count == 0
                    ? 0
                    : await ExecuteAsync(
                        connection, transaction, ScheduleTable.RetireSql(_schedules, retired.Count), command => BindIds(command, "@retired", retired), ct)
                        .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedSchedule>> ClaimSchedulesAsync(
        string claimant, IReadOnlyList<string> ids, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimant);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                var claimed = await RelationalSqlBatch.ReadWrittenRowsAsync(
                    connection, ScheduleTable.ClaimSql(_schedules, ids.Count), command => BindScheduleClaim(command, claimant, ids, lease), ReadClaimedSchedule, cancellationToken)
                    .ConfigureAwait(false);

                return [.. claimed.OrderBy(schedule => schedule.ScheduledFor).ThenBy(schedule => schedule.Id, StringComparer.Ordinal)];
            }
        }
    }

    public Task<bool> AdvanceScheduleAsync(ScheduleAdvance advance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(advance);
        ArgumentNullException.ThrowIfNull(advance.Events);

        return InTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(connection, transaction, ScheduleTable.AdvanceSql(_schedules), command => BindAdvance(command, advance), ct).ConfigureAwait(false) == 1
                && await AppendedAsync(connection, transaction, Envelopes(advance.Events), ct).ConfigureAwait(false),
            advanced => advanced,
            cancellationToken);
    }

    public Task AppendRunAsync(IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        return WriteAsync((connection, transaction, ct) => AppendedAsync(connection, transaction, Envelopes(events), ct), cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await ReadAsync(ScheduleTable.AllSql(_schedules), _ => { }, ReadStoredSchedule, cancellationToken).ConfigureAwait(false);

        return [.. schedules.OrderBy(schedule => schedule.Id, StringComparer.Ordinal)];
    }

    public Task<bool> StartMigrationAsync(
        string id, int revision, IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(events);

        return InTransactionAsync(
            async (connection, transaction, ct) =>
                await ExecuteAsync(connection, transaction, DataMigrationTable.StartSql(_migrations), command => BindMigration(command, id, revision), ct).ConfigureAwait(false) == 1
                && await AppendedAsync(connection, transaction, Envelopes(events), ct).ConfigureAwait(false),
            started => started,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> StartedMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await ReadAsync(DataMigrationTable.StartedSql(_migrations), _ => { }, reader => reader.GetString(0), cancellationToken).ConfigureAwait(false);

        return [.. ids.Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<AlvoEvent> Envelopes(IReadOnlyList<AlvoFrameworkEnvelope> events) =>
        events.Select(framework => (framework ?? throw new ArgumentException("A run's events cannot contain null.", nameof(events))).Envelope);

    private static void BindSchedule(DbCommand command, ScheduleDefinition schedule)
    {
        RelationalSqlBatch.AddParameter(command, "@id", schedule.Id);
        RelationalSqlBatch.AddParameter(command, "@cron", schedule.Cron);
        RelationalSqlBatch.AddParameter(command, "@time_zone", schedule.TimeZone);
        RelationalSqlBatch.AddParameter(command, "@next_run_at", Instant(schedule.NextRunAt));
    }

    private static void BindIds(DbCommand command, string prefix, IReadOnlyList<string> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            RelationalSqlBatch.AddParameter(command, $"{prefix}{index}", ids[index]);
        }
    }

    private void BindScheduleClaim(DbCommand command, string claimant, IReadOnlyList<string> ids, TimeSpan lease)
    {
        BindIds(command, "@id", ids);
        var now = _time.GetUtcNow();
        RelationalSqlBatch.AddParameter(command, "@now", Instant(now));
        RelationalSqlBatch.AddParameter(command, "@lease_until", Instant(now + lease));
        RelationalSqlBatch.AddParameter(command, "@claimant", claimant);
    }

    private static void BindAdvance(DbCommand command, ScheduleAdvance advance)
    {
        RelationalSqlBatch.AddParameter(command, "@id", advance.Id);
        RelationalSqlBatch.AddParameter(command, "@claimant", advance.Claimant);
        RelationalSqlBatch.AddParameter(command, "@scheduled_for", Instant(advance.ScheduledFor));
        RelationalSqlBatch.AddParameter(command, "@next_run_at", Instant(advance.NextRunAt));
    }

    private void BindMigration(DbCommand command, string id, int revision)
    {
        RelationalSqlBatch.AddParameter(command, "@id", id);
        RelationalSqlBatch.AddParameter(command, "@revision", revision);
        RelationalSqlBatch.AddParameter(command, "@started_at", Instant(_time.GetUtcNow()));
    }

    private static ClaimedSchedule ReadClaimedSchedule(DbDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ScheduledFor = InstantOf(reader.GetString(1)),
    };

    private static StoredSchedule ReadStoredSchedule(DbDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Cron = reader.GetString(1),
        TimeZone = reader.GetString(2),
        NextRunAt = InstantOf(reader.GetString(3)),
        LastRunAt = reader.IsDBNull(4) ? null : InstantOf(reader.GetString(4)),
    };
}
