using System.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>Runs an ordered list of SQL commands, either in a caller's transaction or in its own.</summary>
internal static class RelationalSqlBatch
{
    /// <summary>Opens <paramref name="connection"/>, runs <paramref name="sql"/> in one transaction, and commits.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, CancellationToken ct)
    {
        await OpenAsync(connection, ct).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, sql, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="sql"/> against an already-open connection within <paramref name="transaction"/>.</summary>
    public static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<string> sql, DbTransaction transaction, CancellationToken ct)
    {
        foreach (var commandText in sql)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = commandText;
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens <paramref name="connection"/> if it is not already open.</summary>
    public static async Task OpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Adds a named, valued parameter to <paramref name="command"/>.</summary>
    /// <remarks>
    /// Shared by <see cref="EfCoreDescriptorVersionStore"/> and <see cref="VersionRowWriter"/> so
    /// both bind their SQL parameters identically instead of each keeping its own copy.
    /// </remarks>
    public static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
