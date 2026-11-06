using CsCheck;
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// HX-11 acceptance (5): a text constant in a validation never escapes the CHECK it becomes. For any text the literal
/// writer accepts, the engine's own constraint refuses exactly that text and admits a different one, and the table a
/// payload names survives — asked of the engine, the only decoder whose answer counts. Any text it must decline renders
/// no constraint at all.
/// </summary>
public sealed class SqliteCheckLiteralPropertyTests : IDisposable
{
    /// <summary>
    /// The seed of the property's first draw (Ruling 50); CsCheck draws the rest at random, so the accepted/declined floors
    /// are statistical, set far below their expected counts, and a failure prints the seed that replays it.
    /// </summary>
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

    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteCheckLiteralPropertyTests() => _connection.Open();

    [Fact]
    public void A_literal_never_escapes_a_check()
    {
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
                return Holds(text);
            },
            iter: 500,
            threads: 1,
            seed: Seed);
        held.ShouldBeGreaterThanOrEqualTo(250, "the property must reach text a literal carries");
        declined.ShouldBeGreaterThanOrEqualTo(100, "and text it declines");
    }

    [Theory]
    [InlineData("'); DROP TABLE sentinel; --")]
    [InlineData("x' OR 1=1); DROP TABLE sentinel; --")]
    [InlineData("\\'); DROP TABLE sentinel; --")]
    [InlineData("\\\\'); DROP TABLE sentinel; --")]
    [InlineData("E'\\'); DROP TABLE sentinel; --")]
    [InlineData("$$); DROP TABLE sentinel; $$")]
    [InlineData("*/); DROP TABLE sentinel; /*")]
    public void A_payload_naming_the_sentinel_never_escapes_a_check(string payload) =>
        Holds(payload).ShouldBeTrue();

    [Theory]
    [InlineData("total >= -9223372036854775808", "-9223372036854775808", true)]
    [InlineData("total > -9223372036854775808", "-9223372036854775808", false)]
    [InlineData("total > -9223372036854775808", "-9223372036854775807", true)]
    [InlineData("amount <= 79228162514264337593543950335.0", "79228162514264337593543950335", true)]
    [InlineData("amount < 79228162514264337593543950335.0", "79228162514264337593543950335", false)]
    [InlineData("amount < 79228162514264337593543950335.0", "79228162514263337593543950335", true)]
    public void A_boundary_number_is_a_literal_the_engine_reads_as_that_number(string rule, string value, bool admitted)
    {
        var column = rule.Split(' ')[0];
        Recreate(Render(rule), column);

        Inserts(column, column == "total" ? long.Parse(value, CultureInfo.InvariantCulture) : value).ShouldBe(admitted);
    }

    public void Dispose() => _connection.Dispose();

    private string Render(string rule) =>
        ((IPredicateRenderer)new SqlPredicateRenderer()).RenderConstraint(
            new CelCompiler().Compile(rule, CelProfile.Rule, _notes).Expression!, _fields);

    private bool Holds(string text)
    {
        Recreate(Render($"title != {CelText.StringLiteral(text)}"), "title");
        return !Inserts("title", text) && Inserts("title", text + "x") && SentinelSurvives();
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

    private void Recreate(string check, string column)
    {
        Execute("DROP TABLE IF EXISTS notes; CREATE TABLE IF NOT EXISTS sentinel (id INTEGER);");
        Execute(
            "CREATE TABLE notes (title TEXT, total INTEGER, amount TEXT, "
            + $"CONSTRAINT ck_notes CHECK (\"{column}\" IS NULL OR {check}))");
    }

    private bool Inserts(string column, object value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"INSERT INTO notes (\"{column}\") VALUES ($value)";
        command.Parameters.AddWithValue("$value", value);
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException failure) when (failure.SqliteExtendedErrorCode == 275)
        {
            return false;
        }
    }

    private bool SentinelSurvives()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sentinel'";
        return (long)command.ExecuteScalar()! == 1;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
