using CsCheck;
using Microsoft.Data.Sqlite;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// HX-11 acceptance (5): a text constant in a validation never escapes the CHECK it becomes. For any text the literal
/// writer accepts, the engine's own constraint refuses exactly that text and admits a different one, and the table a
/// payload names survives — asked of the engine, the only decoder whose answer counts.
/// </summary>
public sealed class SqliteCheckLiteralPropertyTests : IDisposable
{
    private static readonly EntitySchema _notes = new()
    {
        Name = "notes",
        Fields = [new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true }],
    };

    private readonly IFieldSqlRenderer _fields = new SqliteFieldSqlRenderer();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteCheckLiteralPropertyTests() => _connection.Open();

    [Fact]
    public void A_literal_never_escapes_a_check()
    {
        long held = 0;
        LiteralText.Any().Where(text => !LiteralText.MustBeDeclined(text)).Sample(
            text =>
            {
                var check = ((IPredicateRenderer)new SqlPredicateRenderer()).RenderConstraint(
                    new CelCompiler().Compile($"title != {CelText.StringLiteral(text)}", CelProfile.Rule, _notes).Expression!, _fields);
                Recreate(check);
                held++;
                return !Inserts(text) && Inserts(text + "x") && SentinelSurvives();
            },
            iter: 500,
            threads: 1);
        held.ShouldBe(500);
    }

    public void Dispose() => _connection.Dispose();

    private void Recreate(string check)
    {
        Execute("DROP TABLE IF EXISTS notes; CREATE TABLE IF NOT EXISTS sentinel (id INTEGER);");
        Execute($"CREATE TABLE notes (title TEXT, CONSTRAINT ck_notes_title CHECK (\"title\" IS NULL OR {check}))");
    }

    private bool Inserts(string title)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO notes (title) VALUES ($title)";
        command.Parameters.AddWithValue("$title", title);
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
