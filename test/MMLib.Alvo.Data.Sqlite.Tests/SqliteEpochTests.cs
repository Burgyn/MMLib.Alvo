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
    [InlineData("'0001-01-01'", -62_135_596_800_000_000L)]
    [InlineData("'0001-01-01 00:00:00.001+00:00'", -62_135_596_799_999_000L)]
    public void A_stored_instant_reads_as_microseconds_rounded_to_the_millisecond(string literal, long microseconds) =>
        Scalar($"SELECT {_fields.RenderEpochMicroseconds(literal)}").ShouldBe(microseconds);

    /// <summary>
    /// Exact at every millisecond across CEL's range (review I1): seconds × 10⁶ as a double passes 2⁵³ outside ≈1685–2255,
    /// so each of these read a few microseconds off when the rounding was done in microseconds rather than milliseconds.
    /// </summary>
    /// <param name="literal">A stored, millisecond-aligned instant far from 1970.</param>
    /// <param name="microseconds">Its exact microseconds since the Unix epoch.</param>
    [Theory]
    [InlineData("'9999-12-31 23:59:59.999+00:00'", 253_402_300_799_999_000L)]
    [InlineData("'9999-12-31 23:59:58.001+00:00'", 253_402_300_798_001_000L)]
    [InlineData("'9453-08-05 11:06:02.139+00:00'", 236_159_406_362_139_000L)]
    [InlineData("'1406-04-27 03:17:05.678+00:00'", -17_788_106_574_322_000L)]
    [InlineData("'0840-10-18 03:28:19.268+00:00'", -35_634_198_700_732_000L)]
    public void A_stored_instant_far_from_1970_is_still_exact_to_the_millisecond(string literal, long microseconds) =>
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
