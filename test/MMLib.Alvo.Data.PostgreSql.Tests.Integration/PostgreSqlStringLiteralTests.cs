using CsCheck;
using MMLib.Alvo.Expressions;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL itself reads a computed field's text constant back as exactly the text it was rendered from — under
/// <c>standard_conforming_strings = on</c> <b>and</b> <c>off</c>, which is the reason this dialect writes escape
/// strings rather than the standard literal SQLite gets.
/// </summary>
/// <remarks>
/// The <c>off</c> leg is the one that matters: it is the setting under which a standard literal holding a backslash
/// followed by a quote is broken out of. The <c>on</c> leg proves the default server reads them the same way.
/// </remarks>
public sealed class PostgreSqlStringLiteralTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Gen<string> _hostileText =
        Gen.OneOf(
            Gen.Char["abcXYZ01_ '\"%;-()\\/*$ENé中ž"],
            Gen.Char[' ', '퟿'],
            Gen.Const('\''),
            Gen.Const('\\')).Array[0, 24].Select(characters => new string(characters));

    private readonly IFieldSqlRenderer _fields = new PostgreSqlFieldSqlRenderer();

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task The_engine_reads_every_literal_back_as_the_text_it_was_rendered_from(string standardConformingStrings)
    {
        EnsureEngineAvailable();
        await using var connection = await OpenAsync(standardConformingStrings);
        long asked = 0;

        _hostileText.Sample(
            value =>
            {
                var literal = _fields.RenderStringLiteral(value);
                if (literal is null)
                {
                    return false;
                }

                asked++;
                return ReadBack(connection, literal) == value;
            },
            iter: 300,
            threads: 1);

        asked.ShouldBe(300, "every generated value is text PostgreSQL carries, so none may be declined");
    }

    /// <summary>The payload that breaks a standard literal under <c>off</c> lands as data here, and runs nothing.</summary>
    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task A_backslash_quote_payload_lands_as_data_and_runs_nothing(string standardConformingStrings)
    {
        EnsureEngineAvailable();
        await using var connection = await OpenAsync(standardConformingStrings);
        var sentinel = $"sentinel_{Guid.NewGuid():N}";
        Execute(connection, $"CREATE TABLE \"{sentinel}\" (id integer)");
        var payload = $"\\'); DROP TABLE \"{sentinel}\"; --";

        ReadBack(connection, _fields.RenderStringLiteral(payload)!).ShouldBe(payload);
        ReadBack(connection, $"(SELECT COUNT(*) FROM \"{sentinel}\")::text").ShouldBe("0", "the table is still there");
    }

    private static void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    private async Task<NpgsqlConnection> OpenAsync(string standardConformingStrings)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Execute(connection, $"SET standard_conforming_strings = {standardConformingStrings}");
        ReadBack(connection, "current_setting('standard_conforming_strings')")
            .ShouldBe(standardConformingStrings, "the leg under test is the setting the server really has");
        return connection;
    }

    private static string? ReadBack(NpgsqlConnection connection, string sqlExpression)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {sqlExpression}";
        return command.ExecuteScalar() as string;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
