using MMLib.Alvo.Scheduling;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The timer table's writes on a connection and transaction the caller holds — shared by <see cref="EfCoreTimerStore"/> and by
/// a data write arming its entity's declarative timers, so both are one SQL with one binding.
/// </summary>
/// <remarks>
/// A data write arms through here rather than through <see cref="ITimerStore.UpsertAsync"/>: the store joins the unit open on
/// the flow, which the write already holds, and a write outside any unit has none — the write's own transaction is the one the
/// timer must commit or roll back with.
/// </remarks>
internal static class TimerWrites
{
    /// <summary>Arms <paramref name="timer"/>, replacing one of the same scope and key, unleased, at <paramref name="generation"/>.</summary>
    /// <returns>The rows written.</returns>
    internal static Task<int> UpsertAsync(
        DbConnection connection, DbTransaction transaction, string table, TimerEntry timer, Guid generation, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, TimerTable.UpsertSql(table), command => BindTimer(command, timer, generation), ct);

    /// <summary>Removes the timer of one scope and key, whatever its generation.</summary>
    /// <returns>The rows removed.</returns>
    internal static Task<int> CancelAsync(
        DbConnection connection, DbTransaction transaction, string table, string scope, string key, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, TimerTable.CancelSql(table), command => BindKey(command, scope, key), ct);

    /// <summary>Runs one statement on <paramref name="transaction"/>.</summary>
    /// <returns>The rows affected.</returns>
    internal static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Binds a timer's scope, key and generation — the fence a fire deletes at.</summary>
    internal static void BindFence(DbCommand command, string scope, string key, Guid generation)
    {
        BindKey(command, scope, key);
        RelationalSqlBatch.AddParameter(command, "@generation", generation.ToString());
    }

    private static void BindTimer(DbCommand command, TimerEntry timer, Guid generation)
    {
        BindFence(command, timer.Scope, timer.Key, generation);
        RelationalSqlBatch.AddParameter(command, "@due_at", StoredInstant.Text(timer.DueAt.ToUniversalTime()));
        RelationalSqlBatch.AddParameter(command, "@message_type", timer.MessageType);
        RelationalSqlBatch.AddParameter(command, "@payload", timer.Payload);
        RelationalSqlBatch.AddParameter(command, "@provenance", timer.Provenance);
    }

    private static void BindKey(DbCommand command, string scope, string key)
    {
        RelationalSqlBatch.AddParameter(command, "@scope", scope);
        RelationalSqlBatch.AddParameter(command, "@key", key);
    }
}
