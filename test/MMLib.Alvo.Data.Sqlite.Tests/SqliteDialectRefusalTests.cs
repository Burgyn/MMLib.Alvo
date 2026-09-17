using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The two SQLite behaviours that are this driver's own rather than the shared seam's: decoding a refused
/// write out of a message, and the <c>PRAGMA</c>s that bracket a migration and every opened connection.
/// </summary>
/// <remarks>
/// Against a real in-memory connection, because both facts are about what SQLite actually answers. A
/// hand-built <see cref="SqliteException"/> would let the decode agree with a message SQLite never sends —
/// so the unique-violation cases provoke the engine into producing its own.
/// </remarks>
public class SqliteDialectRefusalTests
{
    /// <summary>The dialect under test, constructed fresh for every test.</summary>
    /// <remarks>
    /// An INSTANCE field, not <c>static readonly</c>. Stryker runs every mutant in one test-host process, so
    /// a static initializer captures whichever mutant was active when the type was first loaded and every
    /// later activation is invisible to it — measured here: the <c>PRAGMA foreign_keys = 1</c> mutant died
    /// against a hand-edited source and survived the mutation run until this field stopped being static.
    /// Same family as #142, in the opposite direction.
    /// </remarks>
    private readonly SqliteSqlDialect _dialect = new();

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteException RefusedWrite(string schema, string seed, string offending)
    {
        using var connection = OpenConnection();
        Execute(connection, schema);
        Execute(connection, seed);

        return Should.Throw<SqliteException>(() => Execute(connection, offending));
    }

    /// <summary>
    /// A unique violation names the columns the index covers, with the table qualifier stripped: the shared
    /// path already knows which entity it is writing and resolves the names against it.
    /// </summary>
    [Fact]
    public void A_unique_violation_names_the_columns_without_their_table()
    {
        var refused = RefusedWrite(
            "CREATE TABLE vehicle (plate TEXT, fleet TEXT); CREATE UNIQUE INDEX ix ON vehicle (plate, fleet);",
            "INSERT INTO vehicle VALUES ('AA-1', 'north');",
            "INSERT INTO vehicle VALUES ('AA-1', 'north');");

        var decoded = _dialect.DecodeConstraintViolation(refused);

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.Columns.ShouldBe(["plate", "fleet"]);
    }

    /// <summary>A single-column index is the same answer with one name.</summary>
    [Fact]
    public void A_single_column_unique_violation_names_that_column()
    {
        var refused = RefusedWrite(
            "CREATE TABLE vehicle (plate TEXT UNIQUE);",
            "INSERT INTO vehicle VALUES ('AA-1');",
            "INSERT INTO vehicle VALUES ('AA-1');");

        _dialect.DecodeConstraintViolation(refused)!.Columns.ShouldBe(["plate"]);
    }

    /// <summary>
    /// The primary key is the same failure under a different extended code, and it names its column too.
    /// </summary>
    [Fact]
    public void A_primary_key_violation_is_a_unique_violation()
    {
        var refused = RefusedWrite(
            "CREATE TABLE vehicle (id TEXT PRIMARY KEY);",
            "INSERT INTO vehicle VALUES ('v1');",
            "INSERT INTO vehicle VALUES ('v1');");

        var decoded = _dialect.DecodeConstraintViolation(refused);

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.Columns.ShouldBe(["id"]);
    }

    /// <summary>
    /// A foreign-key violation carries the kind and no columns: SQLite's message names none, and inventing
    /// them would attribute the refusal to a column that did not cause it.
    /// </summary>
    [Fact]
    public void A_foreign_key_violation_carries_the_kind_and_no_columns()
    {
        using var connection = OpenConnection();
        Execute(connection, "PRAGMA foreign_keys = 1;");
        Execute(connection, "CREATE TABLE vehicle (id TEXT PRIMARY KEY);");
        Execute(connection, "CREATE TABLE trip (vehicle TEXT REFERENCES vehicle(id));");

        var refused = Should.Throw<SqliteException>(
            () => Execute(connection, "INSERT INTO trip VALUES ('missing');"));

        var decoded = _dialect.DecodeConstraintViolation(refused);

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        decoded.Columns.ShouldBeEmpty();
    }

    /// <summary>A refusal that is not a constraint failure is not decoded as one.</summary>
    [Fact]
    public void A_failure_that_is_not_a_constraint_answers_nothing()
    {
        using var connection = OpenConnection();

        var refused = Should.Throw<SqliteException>(
            () => Execute(connection, "SELECT * FROM no_such_table;"));

        _dialect.DecodeConstraintViolation(refused).ShouldBeNull();
    }


    /// <summary>
    /// A migration is bracketed by turning foreign keys off and back ON. The closing pragma is the one that
    /// matters: SQLite scopes <c>foreign_keys</c> to the connection, and EF pools connections, so leaving it
    /// off would silently stop enforcing foreign keys for whatever ran next on that connection.
    /// </summary>
    [Fact]
    public void A_migration_is_bracketed_by_turning_foreign_keys_off_and_back_on()
    {
        _dialect.MigrationFraming.Before.ShouldBe(["PRAGMA foreign_keys = 0"]);
        _dialect.MigrationFraming.After.ShouldBe(["PRAGMA foreign_keys = 1"]);
    }

    /// <summary>
    /// And the framing is executable rather than merely well-spelled — a pragma SQLite rejects would leave
    /// the migration batch failing at run time with nothing here to catch it.
    /// </summary>
    [Fact]
    public void Both_framing_pragmas_are_statements_sqlite_accepts()
    {
        using var connection = OpenConnection();

        foreach (var statement in _dialect.MigrationFraming.Before.Concat(_dialect.MigrationFraming.After))
        {
            Execute(connection, statement);
        }

        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA foreign_keys;";
        read.ExecuteScalar().ShouldBe(1L);
    }

    /// <summary>
    /// Every opened connection gets <c>case_sensitive_like</c> ON, on both the sync and the async path: EF
    /// calls whichever suits it, and a filter that is case-sensitive on one and not the other would answer a
    /// different row set for the same rule.
    /// </summary>
    [Fact]
    public void An_opened_connection_is_made_case_sensitive_for_like()
    {
        using var connection = OpenConnection();

        new SqliteCaseSensitiveLike().ConnectionOpened(connection, null!);

        LikeIsCaseSensitive(connection).ShouldBeTrue();
    }

    /// <summary>The async path applies the same pragma.</summary>
    [Fact]
    public async Task An_asynchronously_opened_connection_is_made_case_sensitive_for_like()
    {
        using var connection = OpenConnection();

        await new SqliteCaseSensitiveLike()
            .ConnectionOpenedAsync(connection, null!, TestContext.Current.CancellationToken);

        LikeIsCaseSensitive(connection).ShouldBeTrue();
    }

    /// <summary>Without the interceptor SQLite's default is case-INsensitive, which is what makes the two
    /// facts above worth asserting rather than tautological.</summary>
    [Fact]
    public void An_untouched_connection_is_case_insensitive_for_like()
    {
        using var connection = OpenConnection();

        LikeIsCaseSensitive(connection).ShouldBeFalse();
    }

    /// <summary>Both interceptor paths refuse a missing connection rather than dereferencing it.</summary>
    [Fact]
    public void The_connection_is_required_on_both_paths()
    {
        var interceptor = new SqliteCaseSensitiveLike();

        Should.Throw<ArgumentNullException>(() => interceptor.ConnectionOpened(null!, null!))
            .ParamName.ShouldBe("connection");
        Should.Throw<ArgumentNullException>(
                () => interceptor.ConnectionOpenedAsync(null!, null!, TestContext.Current.CancellationToken))
            .ParamName.ShouldBe("connection");
    }

    /// <summary>
    /// SQLite reports an extended code for a constraint it raised itself, but the decode also has a
    /// fallback for a bare <c>SQLITE_CONSTRAINT</c> (19), which is what an older engine or a wrapper can
    /// surface. That path is unreachable from a real refusal, so it is driven by constructing the exception
    /// the way such a caller would.
    /// </summary>
    /// <param name="message">The message the fallback has to read the columns out of.</param>
    /// <param name="expected">The bare column names it must answer.</param>
    [Theory]
    [InlineData("UNIQUE constraint failed: vehicle.plate", new[] { "plate" })]
    [InlineData("UNIQUE constraint failed: vehicle.plate, vehicle.fleet", new[] { "plate", "fleet" })]
    [InlineData("UNIQUE constraint failed: plate", new[] { "plate" })]
    public void A_bare_constraint_code_still_decodes_the_unique_columns(string message, string[] expected)
    {
        var decoded = _dialect.DecodeConstraintViolation(new SqliteException(message, Constraint));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.Columns.ShouldBe(expected);
    }

    /// <summary>The same fallback recognises a foreign-key refusal, which names no column.</summary>
    [Fact]
    public void A_bare_constraint_code_decodes_a_foreign_key_refusal()
    {
        var decoded = _dialect.DecodeConstraintViolation(
            new SqliteException("FOREIGN KEY constraint failed", Constraint));

        decoded.ShouldNotBeNull();
        decoded.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        decoded.Columns.ShouldBeEmpty();
    }

    /// <summary>
    /// A constraint failure of a kind the decode does not claim — <c>CHECK</c>, <c>NOT NULL</c> — answers
    /// nothing rather than being reported as a unique violation against no columns.
    /// </summary>
    /// <param name="message">A constraint message that is neither unique nor foreign key.</param>
    [Theory]
    [InlineData("CHECK constraint failed: vehicle")]
    [InlineData("NOT NULL constraint failed: vehicle.plate")]
    public void A_constraint_failure_of_another_kind_answers_nothing(string message)
        => _dialect.DecodeConstraintViolation(new SqliteException(message, Constraint)).ShouldBeNull();

    /// <summary>And a code that is not a constraint at all is not read for columns.</summary>
    [Fact]
    public void A_code_that_is_not_a_constraint_answers_nothing()
        => _dialect.DecodeConstraintViolation(new SqliteException("no such table: vehicle", 1)).ShouldBeNull();

    /// <summary>
    /// The column name is guarded like the two arguments beside it: a generated column is DDL, so a blank
    /// name would surface as a migration failure with no author to attribute it to.
    /// </summary>
    /// <param name="columnName">A name no column can have.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_generated_column_refuses_a_blank_column_name(string columnName)
        => Should.Throw<ArgumentException>(
                () => _dialect.GeneratedColumnDefinition(columnName, "TEXT", "unit_price * amount"))
            .ParamName.ShouldBe("columnName");

    /// <summary><c>SQLITE_CONSTRAINT</c>, the bare code with no extended part.</summary>
    private const int Constraint = 19;

    private static bool LikeIsCaseSensitive(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'A' LIKE 'a';";
        return (long)command.ExecuteScalar()! == 0L;
    }

}
