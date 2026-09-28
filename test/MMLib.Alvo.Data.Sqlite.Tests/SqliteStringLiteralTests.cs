using CsCheck;
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite reads the text constants a computed field writes into DDL exactly as they were authored — asked of the
/// engine itself, which is the only decoder whose answer counts.
/// </summary>
/// <remarks>
/// SQLite's string constant is the standard's: <i>"A string constant is formed by enclosing the string in single
/// quotes. A single quote within the string can be encoded by putting two single quotes in a row … C-style escapes
/// using the backslash character are not supported because they are not standard SQL"</i> (lang_expr.html). So a
/// backslash is an ordinary character here and needs no escaping of its own.
/// </remarks>
public sealed class SqliteStringLiteralTests : IDisposable
{
    private static readonly Gen<string> _hostileText =
        Gen.OneOf(
            Gen.Char["abcXYZ01_ '\"%;-()\\/*$ENé中ž"],
            Gen.Char[' ', '퟿'],
            Gen.Const('\'')).Array[0, 24].Select(characters => new string(characters));

    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteStringLiteralTests() => _connection.Open();

    [Fact]
    public void The_engine_reads_every_literal_back_as_the_text_it_was_rendered_from()
    {
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
                return ReadBack(literal) == value;
            },
            iter: 2_000,
            threads: 1);

        asked.ShouldBe(2_000, "every generated value is text SQLite carries, so none may be declined");
    }

    /// <summary>
    /// A breakout attempt lands as data: the statement is still one <c>SELECT</c> of one value, and the table the
    /// payload names is still there afterwards.
    /// </summary>
    [Theory]
    [InlineData("'); DROP TABLE sentinel; --")]
    [InlineData("\\'); DROP TABLE sentinel; --")]
    [InlineData("' || (SELECT 'leak') || '")]
    public void A_payload_lands_as_data_and_runs_nothing(string payload)
    {
        Execute("CREATE TABLE sentinel (id INTEGER)");

        ReadBack(_fields.RenderStringLiteral(payload)!).ShouldBe(payload);
        ReadBack("(SELECT COUNT(*) FROM sentinel)").ShouldBe("0", "the table the payload tried to drop is still there");
    }

    [Fact]
    public void A_concatenation_is_the_standards_operator()
        => _fields.RenderStringConcatenation("\"first_name\"", "' '").ShouldBe("(\"first_name\" || ' ')");

    public void Dispose() => _connection.Dispose();

    private string? ReadBack(string sqlExpression)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT CAST({sqlExpression} AS TEXT)";
        return command.ExecuteScalar() as string;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
