using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Npgsql;
using System.Globalization;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// HX-11 acceptance (5) on PostgreSQL: a text constant in a validation never escapes the CHECK it becomes. For any text the
/// literal writer accepts, the engine's own constraint refuses exactly that text and admits a different one, and the table a
/// payload names survives — under <c>standard_conforming_strings</c> on and off, asked of the engine. Any text it must
/// decline renders no constraint at all.
/// </summary>
/// <remarks>
/// Every drawn or fixed payload that names <c>sentinel</c> is rewritten to name this instance's sentinel table, so the
/// survival check is about the table a payload would really drop.
/// </remarks>
public sealed class PostgreSqlCheckLiteralPropertyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    /// <summary>The property's pinned seed (Ruling 50), so its accepted/declined floors hold run to run.</summary>
    private const string Seed = "0N0XIzNsQ0O2";

    private static readonly EntitySchema _notes = new()
    {
        Name = "notes",
        Fields =
        [
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "total", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "amount", Type = FieldType.Decimal, Nullable = true },
        ],
    };

    private readonly IFieldSqlRenderer _fields = new PostgreSqlFieldSqlRenderer();
    private readonly string _table = $"notes_{Guid.NewGuid():N}";
    private readonly string _sentinel = $"sentinel_{Guid.NewGuid():N}";

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task A_literal_never_escapes_a_check(string standardConformingStrings)
    {
        await using var connection = await OpenAsync(standardConformingStrings);
        long held = 0;
        long declined = 0;

        LiteralText.Hostile().Sample(
            text =>
            {
                if (LiteralText.MustBeDeclined(text))
                {
                    declined++;
                    return RendersNothing(text);
                }

                held++;
                return Holds(connection, text);
            },
            iter: 300,
            threads: 1,
            seed: Seed);
        held.ShouldBeGreaterThanOrEqualTo(150, "the property must reach text a literal carries");
        declined.ShouldBeGreaterThanOrEqualTo(60, "and text it declines");
    }

    [Theory]
    [InlineData("on", "'); DROP TABLE sentinel; --")]
    [InlineData("off", "'); DROP TABLE sentinel; --")]
    [InlineData("on", "\\'); DROP TABLE sentinel; --")]
    [InlineData("off", "\\'); DROP TABLE sentinel; --")]
    [InlineData("on", "\\\\'); DROP TABLE sentinel; --")]
    [InlineData("off", "\\\\'); DROP TABLE sentinel; --")]
    [InlineData("on", "E'\\'); DROP TABLE sentinel; --")]
    [InlineData("off", "E'\\'); DROP TABLE sentinel; --")]
    [InlineData("on", "'||E'\\x27'); DROP TABLE sentinel; --")]
    [InlineData("off", "'||E'\\x27'); DROP TABLE sentinel; --")]
    [InlineData("on", "$$); DROP TABLE sentinel; $$")]
    [InlineData("off", "$$); DROP TABLE sentinel; $$")]
    public async Task A_payload_naming_the_sentinel_never_escapes_a_check(string standardConformingStrings, string payload)
    {
        await using var connection = await OpenAsync(standardConformingStrings);

        Holds(connection, payload).ShouldBeTrue();
    }

    [Theory]
    [InlineData("total >= -9223372036854775808", "-9223372036854775808", true)]
    [InlineData("total > -9223372036854775808", "-9223372036854775808", false)]
    [InlineData("total > -9223372036854775808", "-9223372036854775807", true)]
    [InlineData("amount <= 79228162514264337593543950335.0", "79228162514264337593543950335", true)]
    [InlineData("amount < 79228162514264337593543950335.0", "79228162514264337593543950335", false)]
    [InlineData("amount < 79228162514264337593543950335.0", "79228162514264337593543950334", true)]
    public async Task A_boundary_number_is_a_literal_the_engine_reads_as_that_number(string rule, string value, bool admitted)
    {
        await using var connection = await OpenAsync("on");
        var column = rule.Split(' ')[0];
        Recreate(connection, Render(rule), column);

        object parameter = column == "total"
            ? long.Parse(value, CultureInfo.InvariantCulture)
            : decimal.Parse(value, CultureInfo.InvariantCulture);
        Inserts(connection, column, parameter).ShouldBe(admitted);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrEmpty(fixture.ConnectionString))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Execute(connection, $"DROP TABLE IF EXISTS \"{_table}\"; DROP TABLE IF EXISTS \"{_sentinel}\"");
    }

    private async Task<NpgsqlConnection> OpenAsync(string standardConformingStrings)
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(fixture.ConnectionString),
            "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Execute(connection, $"SET standard_conforming_strings = {standardConformingStrings}");
        Execute(connection, $"CREATE TABLE IF NOT EXISTS \"{_sentinel}\" (id integer)");
        using var command = new NpgsqlCommand("SELECT current_setting('standard_conforming_strings')", connection);
        ((string?)command.ExecuteScalar()).ShouldBe(standardConformingStrings, "the leg under test is the setting the server really has");
        return connection;
    }

    private string Render(string rule) =>
        ((IPredicateRenderer)new SqlPredicateRenderer()).RenderConstraint(
            new CelCompiler().Compile(rule, CelProfile.Rule, _notes).Expression!, _fields);

    private bool Holds(NpgsqlConnection connection, string drawn)
    {
        var text = drawn.Replace("sentinel", _sentinel, StringComparison.Ordinal);
        Recreate(connection, Render($"title != {CelText.StringLiteral(text)}"), "title");
        return !Inserts(connection, "title", text) && Inserts(connection, "title", text + "x") && SentinelSurvives(connection);
    }

    private bool RendersNothing(string text)
    {
        try
        {
            Render($"title != {CelText.StringLiteral(text)}");
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private void Recreate(NpgsqlConnection connection, string check, string column) =>
        Execute(
            connection,
            $"DROP TABLE IF EXISTS \"{_table}\"; "
            + $"CREATE TABLE \"{_table}\" (title text, total bigint, amount numeric, "
            + $"CONSTRAINT ck_notes CHECK (\"{column}\" IS NULL OR {check}))");

    private bool Inserts(NpgsqlConnection connection, string column, object value)
    {
        using var command = new NpgsqlCommand($"INSERT INTO \"{_table}\" (\"{column}\") VALUES (@value)", connection);
        command.Parameters.AddWithValue(nameof(value), value);
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
