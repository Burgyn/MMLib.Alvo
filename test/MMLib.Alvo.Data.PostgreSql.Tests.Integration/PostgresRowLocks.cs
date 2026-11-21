using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The row-lock staging the PostgreSQL race facts share: hold rows in a transaction left open, wait until store calls
/// queue behind them, and probe from a third connection which rows a waiting call already holds.
/// </summary>
internal static class PostgresRowLocks
{
    private static readonly TimeSpan _lockWait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs <paramref name="statements"/> in a transaction left open, so the rows they lock stay locked.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="statements">Each statement, run in order; a <c>SELECT … FOR UPDATE</c> locks without a new row version.</param>
    internal static async Task<HeldTransaction> HoldAsync(IServiceProvider services, params string[] statements)
    {
        var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new HeldTransaction(connection, transaction);
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> backends of this database wait on a lock, and fails if any of
    /// <paramref name="watched"/> completes first — a call that finished never queued where the fact needs it to.
    /// </summary>
    /// <param name="services">The host container.</param>
    /// <param name="count">How many waiting backends the staging needs.</param>
    /// <param name="because">What a call finishing first would mean.</param>
    /// <param name="watched">The store calls that must queue.</param>
    internal static async Task UntilWaitingAsync(IServiceProvider services, int count, string because, params Task[] watched)
    {
        var deadline = DateTimeOffset.UtcNow + _lockWait;
        while (await WaitersAsync(services) < count)
        {
            watched.ShouldAllBe(task => !task.IsCompleted, because);
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, $"fewer than {count} store calls ever waited on a lock");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    /// <summary>
    /// Whether <paramref name="sql"/>, a <c>SELECT … FOR UPDATE NOWAIT</c>, gets its rows at once on a connection of its own —
    /// that is, no other transaction holds any of them. The probe's own locks are rolled back.
    /// </summary>
    /// <param name="services">The host container.</param>
    /// <param name="sql">The probe.</param>
    internal static async Task<bool> FreeAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync(Ct);
            return true;
        }
        catch (Npgsql.PostgresException failure) when (failure.SqlState == "55P03")
        {
            return false;
        }
    }

    /// <summary>Runs <paramref name="sql"/> on its own connection and returns the first column of the first row.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="sql">The statement.</param>
    internal static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>Runs <paramref name="sql"/> on its own connection, committed.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="sql">The statement.</param>
    internal static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> WaitersAsync(IServiceProvider services) => Convert.ToInt64(
        await ScalarAsync(
            services,
            """
            SELECT COUNT(DISTINCT l.pid) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
             WHERE NOT l.granted AND a.datname = current_database()
            """),
        CultureInfo.InvariantCulture);

    /// <summary>An open transaction and its connection, committed by the fact and disposed with it.</summary>
    internal sealed class HeldTransaction(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        /// <summary>Commits, releasing every row the transaction holds at once.</summary>
        /// <param name="ct">A token to cancel the operation.</param>
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
