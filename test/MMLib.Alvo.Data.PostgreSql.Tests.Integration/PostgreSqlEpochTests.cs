using MMLib.Alvo.Expressions;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL's epoch is <c>EXTRACT(EPOCH FROM …)</c>, exact to the microsecond (HX-11 Ruling 11) — asked of the engine.</summary>
public sealed class PostgreSqlEpochTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private readonly IFieldSqlRenderer _fields = new PostgreSqlFieldSqlRenderer();

    [Theory]
    [InlineData("'2026-11-09 10:00:00.123456+00'::timestamptz", 1_794_218_400_123_456L)]
    [InlineData("'2026-11-09 12:00:00.123456+02'::timestamptz", 1_794_218_400_123_456L)]
    [InlineData("'1969-12-31 23:59:59.5+00'::timestamptz", -500_000L)]
    [InlineData("'2026-11-09'::date", 1_794_182_400_000_000L)]
    public async Task A_stored_instant_reads_as_exact_microseconds(string literal, long microseconds) =>
        (await ScalarAsync($"SELECT {_fields.RenderEpochMicroseconds(literal)}")).ShouldBe(microseconds);

    [Fact]
    public async Task An_empty_instant_is_empty() =>
        (await ScalarAsync($"SELECT {_fields.RenderEpochMicroseconds("NULL::timestamptz")}")).ShouldBe(DBNull.Value);

    private async Task<object?> ScalarAsync(string sql)
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(postgres.ConnectionString),
            "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
