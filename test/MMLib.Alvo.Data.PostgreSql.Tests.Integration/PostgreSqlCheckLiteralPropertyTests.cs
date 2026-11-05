using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// HX-11 acceptance (5) on PostgreSQL: a text constant in a validation never escapes the CHECK it becomes. For any text the
/// literal writer accepts, the engine's own constraint refuses exactly that text and admits a different one, and the table a
/// payload names survives — under <c>standard_conforming_strings</c> on and off, asked of the engine.
/// </summary>
/// <remarks>
/// NUL is excluded from the drawn text, as <c>TypedLayerFacts</c> excludes it: PostgreSQL refuses NUL in a bound parameter
/// at all, which is the engine refusing the value the test inserts, not text leaking from the constraint.
/// </remarks>
public sealed class PostgreSqlCheckLiteralPropertyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly EntitySchema _notes = new()
    {
        Name = "notes",
        Fields = [new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true }],
    };

    private readonly IFieldSqlRenderer _fields = new PostgreSqlFieldSqlRenderer();
    private readonly string _table = $"notes_{Guid.NewGuid():N}";
    private readonly string _sentinel = $"sentinel_{Guid.NewGuid():N}";

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task A_literal_never_escapes_a_check(string standardConformingStrings)
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(fixture.ConnectionString),
            "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        await using var connection = await OpenAsync(standardConformingStrings);
        Execute(connection, $"CREATE TABLE \"{_sentinel}\" (id integer)");
        long held = 0;

        LiteralText.Any().Where(text => !LiteralText.MustBeDeclined(text) && !text.Contains('\0', StringComparison.Ordinal)).Sample(
            text =>
            {
                var check = ((IPredicateRenderer)new SqlPredicateRenderer()).RenderConstraint(
                    new CelCompiler().Compile($"title != {CelText.StringLiteral(text)}", CelProfile.Rule, _notes).Expression!, _fields);
                Recreate(connection, check);
                held++;
                return !Inserts(connection, text) && Inserts(connection, text + "x") && SentinelSurvives(connection);
            },
            iter: 300,
            threads: 1);
        held.ShouldBe(300);
    }

    private async Task<NpgsqlConnection> OpenAsync(string standardConformingStrings)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Execute(connection, $"SET standard_conforming_strings = {standardConformingStrings}");
        using var command = new NpgsqlCommand("SELECT current_setting('standard_conforming_strings')", connection);
        ((string?)command.ExecuteScalar()).ShouldBe(standardConformingStrings, "the leg under test is the setting the server really has");
        return connection;
    }

    private void Recreate(NpgsqlConnection connection, string check) =>
        Execute(
            connection,
            $"DROP TABLE IF EXISTS \"{_table}\"; "
            + $"CREATE TABLE \"{_table}\" (title text, CONSTRAINT ck_notes_title CHECK (\"title\" IS NULL OR {check}))");

    private bool Inserts(NpgsqlConnection connection, string title)
    {
        using var command = new NpgsqlCommand($"INSERT INTO \"{_table}\" (title) VALUES (@title)", connection);
        command.Parameters.AddWithValue(nameof(title), title);
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return false;
        }
    }

    private bool SentinelSurvives(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand($"SELECT to_regclass('\"{_sentinel}\"') IS NOT NULL", connection);
        return (bool)command.ExecuteScalar()!;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
