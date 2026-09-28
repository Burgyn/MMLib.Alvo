using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using System.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="MigrationFramingScope"/>'s restore: a connection whose restore failed is closed, never left open with
/// enforcement suspended, and on the failing path the work's own exception is the one a caller sees; and the tables
/// its pre-commit check is scoped to, which fail closed (every table) when no step names one.
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

    [Fact]
    public void A_plan_whose_every_step_names_an_empty_entity_is_checked_over_every_table()
    {
        var plan = PlanOver(string.Empty, string.Empty);

        MigrationFramingScope.Touched(plan).ShouldBeNull();
    }

    [Fact]
    public void An_empty_entity_beside_a_named_one_is_dropped_from_the_touched_tables()
    {
        var plan = PlanOver(string.Empty, "orders", "orders");

        MigrationFramingScope.Touched(plan).ShouldBe(["orders"]);
    }

    private static MigrationPlan PlanOver(params string[] entities) => new()
    {
        Steps =
        [
            .. entities.Select(entity => new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.AlterField, Entity = entity }, IsDestructive: false, Reason: null)),
        ],
        Sql = ["SELECT 1"],
    };
}
