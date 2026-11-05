using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's epoch is unixepoch(…, 'subsec'), millisecond-precise (HX-11 Ruling 11) — asked of the engine.</summary>
public sealed class SqliteEpochTests : IDisposable
{
    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteEpochTests() => _connection.Open();

    [Fact]
    public void The_bundled_engine_has_subsecond_unixepoch() =>
        Version.Parse(Scalar("SELECT sqlite_version()")!.ToString()!).ShouldBeGreaterThanOrEqualTo(new Version(3, 42, 0));

    [Theory]
    [InlineData("'2026-11-09 10:00:00.123+00:00'", 1_794_218_400_123_000L)]
    [InlineData("'2026-11-09T12:00:00.123+02:00'", 1_794_218_400_123_000L)]
    [InlineData("'2026-11-09 10:00:00.1234567+00:00'", 1_794_218_400_123_000L)]
    [InlineData("'1969-12-31 23:59:59.5+00:00'", -500_000L)]
    [InlineData("'2026-11-09'", 1_794_182_400_000_000L)]
    public void A_stored_instant_reads_as_microseconds_rounded_to_the_millisecond(string literal, long microseconds) =>
        Scalar($"SELECT {_fields.RenderEpochMicroseconds(literal)}").ShouldBe(microseconds);

    [Fact]
    public void An_empty_instant_is_empty() => Scalar($"SELECT {_fields.RenderEpochMicroseconds("NULL")}").ShouldBe(DBNull.Value);

    public void Dispose() => _connection.Dispose();

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
