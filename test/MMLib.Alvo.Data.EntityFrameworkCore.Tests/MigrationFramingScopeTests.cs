using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using System.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="MigrationFramingScope"/>'s restore: a connection whose restore failed is closed, never left open with
/// enforcement suspended, and on the failing path the work's own exception is the one a caller sees.
/// </summary>
public sealed class MigrationFramingScopeTests
{
    /// <summary>A restore statement the engine refuses — standing in for a connection the batch left broken.</summary>
    private static readonly MigrationBatchFraming _brokenRestore = new()
    {
        Before = ["PRAGMA foreign_keys = 0"],
        After = ["SELECT * FROM no_such_table"],
    };

    [Fact]
    public async Task A_failed_restore_after_the_work_closes_the_connection_and_propagates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");

        await Should.ThrowAsync<SqliteException>(() => MigrationFramingScope.RunAsync(
            connection, _brokenRestore, () => Task.FromResult(true), TestContext.Current.CancellationToken));

        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact]
    public async Task A_failed_restore_after_a_failed_work_closes_the_connection_and_keeps_the_works_exception()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");

        await Should.ThrowAsync<TimeoutException>(() => MigrationFramingScope.RunAsync<bool>(
            connection, _brokenRestore, () => throw new TimeoutException("the work's own failure"),
            TestContext.Current.CancellationToken));

        connection.State.ShouldBe(ConnectionState.Closed);
    }
}
