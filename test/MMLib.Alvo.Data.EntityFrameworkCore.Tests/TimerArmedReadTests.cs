using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The timer re-plan's read of what is already armed (issue 483): an equality on the whole primary key per timer, so it seeks
/// the key, and in statements small enough for an engine's parameter limit.
/// </summary>
public sealed class TimerArmedReadTests
{
    private const string Table = "alvo_timers";
    private const int ThirdEngineParameterLimit = 2100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Each_key_is_an_equality_on_the_whole_primary_key() =>
        TimerTable.ArmedSql(Table, 2).ShouldBe(
            "SELECT scope, timer_key, due_at FROM alvo_timers WHERE (scope = @scope0 AND timer_key = @key0) OR (scope = @scope1 AND timer_key = @key1)");

    [Fact]
    public void One_statement_stays_well_under_a_2100_parameter_engine() =>
        (TimerTable.ArmedKeysPerStatement * 2).ShouldBeLessThan(ThirdEngineParameterLimit);

    [Fact]
    public async Task More_keys_than_one_statement_holds_are_all_read_in_their_own_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await TimerTable.EnsureAsync(connection, Table, Ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(Ct);
        var due = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        var keys = Enumerable.Range(0, TimerTable.ArmedKeysPerStatement + 7).Select(index => ("tenant-a", $"alvo:bookings:reminder:{index}")).ToList();
        foreach (var (scope, key) in keys)
        {
            await InsertAsync(connection, transaction, scope, key, due);
        }

        await InsertAsync(connection, transaction, "tenant-b", keys[0].Item2, due.AddHours(1));

        var armed = await TimerWrites.ArmedAsync(connection, transaction, Table, keys, Ct);

        armed.Count.ShouldBe(keys.Count);
        armed.Values.ShouldAllBe(at => at == due);
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string scope, string key, DateTimeOffset due)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO alvo_timers (scope, timer_key, generation, due_at, message_type, payload, provenance) VALUES ($scope, $key, 'g', $due, 't', '{}', '{}')";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$due", due.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(Ct);
    }
}
