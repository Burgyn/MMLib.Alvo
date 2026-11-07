using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

#pragma warning disable EF1001
#pragma warning disable RCS1194 // a test double, never constructed with a message

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The SQLite dialect's answers that no integration scenario pins on its own — each guard, each decode branch and each
/// statement spelling — so a mutation of any of them is seen by this project alone, which is the only one the post-merge
/// mutation shard runs.
/// </summary>
public sealed class SqliteDialectSeamTests
{
    private const string ExclusionTag = "alvo_exclusion:";

    private readonly SqliteSqlDialect _concrete = new();

    private IAlvoSqlDialect Dialect => _concrete;

    private static ExclusionConstraintDefinition Exclusion(string name) =>
        new("bookings", name, ["room"], "starts_at", "ends_at", FieldType.DateTime, null);

    [Fact]
    public void Two_values_are_distinct_by_the_null_safe_comparison()
        => Dialect.RenderDistinct("\"a\"", "@b").ShouldBe("\"a\" IS NOT @b");

    [Fact]
    public void Generated_columns_are_added_by_rebuilding_the_table_and_counts_by_the_shared_upsert()
    {
        Dialect.GeneratedColumnAddRequiresTableRebuild.ShouldBeTrue();
        Dialect.SequenceAllocationStatement("alvo_sequences").ShouldBe(SequenceTable.Upsert("alvo_sequences"));
    }

    [Fact]
    public void A_table_without_an_entity_is_refused()
        => Should.Throw<ArgumentNullException>(() => _concrete.RenderTable(null!, lockedPreImageFor: null)).ParamName.ShouldBe("entity");

    [Fact]
    public void A_decode_without_a_failure_is_refused()
        => Should.Throw<ArgumentNullException>(() => _concrete.DecodeConstraintViolation(null!)).ParamName.ShouldBe("failure");

    [Theory]
    [InlineData("c", "", "x", "storeType")]
    [InlineData("c", " ", "x", "storeType")]
    [InlineData("c", "TEXT", "", "renderedExpression")]
    [InlineData("c", "TEXT", " ", "renderedExpression")]
    public void A_generated_column_refuses_each_blank_part_by_name(string column, string storeType, string expression, string parameter)
        => Should.Throw<ArgumentException>(() => _concrete.GeneratedColumnDefinition(column, storeType, expression)).ParamName.ShouldBe(parameter);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_null_projection_refuses_a_blank_store_type(string storeType)
        => Should.Throw<ArgumentException>(() => _concrete.RenderNullProjection(storeType)).ParamName.ShouldBe("storeType");

    [Fact]
    public void An_exclusion_without_a_definition_is_refused()
        => Should.Throw<ArgumentNullException>(() => Dialect.ExclusionConstraint(null!)).ParamName.ShouldBe("definition");

    [Theory]
    [InlineData("xc_bookings_6423bd5f")]
    [InlineData("a")]
    [InlineData("z9_")]
    [InlineData("_0")]
    public void A_computed_name_is_accepted(string name)
        => Dialect.ExclusionConstraint(Exclusion(name)).ShouldNotBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("Xc")]
    [InlineData("xC")]
    [InlineData("a-b")]
    [InlineData("a'b")]
    [InlineData("a b")]
    [InlineData("a`")]
    [InlineData("a{")]
    [InlineData("a/")]
    [InlineData("a:")]
    [InlineData("a@")]
    [InlineData("aé")]
    public void A_name_outside_the_computed_alphabet_is_refused_before_it_reaches_the_trigger_text(string name)
        => Should.Throw<ArgumentException>(() => Dialect.ExclusionConstraint(Exclusion(name))).ParamName.ShouldBe("name");

    [Fact]
    public void An_exclusion_is_a_support_index_and_two_triggers_dropped_by_name()
    {
        var definition = Exclusion("xc_b");
        var support = IndexNames.ExclusionSupport(definition);

        var sql = Dialect.ExclusionConstraint(definition).ShouldNotBeNull();

        sql.Create[0].ShouldBe($"CREATE INDEX IF NOT EXISTS \"{support}\" ON \"bookings\" (\"room\", \"starts_at\")");
        sql.Create[1].ShouldStartWith("CREATE TRIGGER IF NOT EXISTS \"xc_b_i\" AFTER INSERT ON \"bookings\"");
        sql.Create[2].ShouldStartWith("CREATE TRIGGER IF NOT EXISTS \"xc_b_u\" AFTER UPDATE ON \"bookings\"");
        sql.Create[1].ShouldContain($"RAISE(ABORT, '{ExclusionTag}xc_b')");
        sql.Create.Count.ShouldBe(3);
        sql.Drop.ShouldBe(
        [
            "DROP TRIGGER IF EXISTS \"xc_b_i\"",
            "DROP TRIGGER IF EXISTS \"xc_b_u\"",
            $"DROP INDEX IF EXISTS \"{support}\"",
        ]);
        sql.SurvivesTableRebuild.ShouldBeFalse();
    }

    [Theory]
    [InlineData(2067)]
    [InlineData(1555)]
    public void A_unique_code_decodes_the_columns_out_of_sqlites_message(int extended)
    {
        var decoded = Decode("SQLite Error 19: 'UNIQUE constraint failed: bookings.tenant_id, bookings.room'.", 19, extended);

        decoded.Kind.ShouldBe(AlvoConstraintKind.Unique);
        decoded.Columns.ShouldBe(["tenant_id", "room"]);
    }

    [Theory]
    [InlineData("boom", new string[0])]
    [InlineData("UNIQUE constraint failed: t., t.b", new[] { "b" })]
    [InlineData("UNIQUE constraint failed: t.a, t.b.", new[] { "a", "b" })]
    [InlineData("UNIQUE constraint failed: t.a,,t.b", new[] { "a", "b" })]
    [InlineData("UNIQUE constraint failed:   t.a ,   t.b  ", new[] { "a", "b" })]
    [InlineData("UNIQUE constraint failed: main.t.a", new[] { "a" })]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: t.a'.", new[] { "a" })]
    public void The_columns_are_read_fail_soft_and_unqualified(string message, string[] expected)
        => Decode(message, 19, 2067).Columns.ShouldBe(expected);

    [Fact]
    public void A_foreign_key_code_is_a_referenced_refusal_whatever_the_message_says()
    {
        var decoded = Decode("boom", 19, 787);

        decoded.Kind.ShouldBe(AlvoConstraintKind.Referenced);
        decoded.Columns.ShouldBeEmpty();
    }

    [Fact]
    public void A_check_code_with_an_unreadable_message_is_a_check_with_no_name()
        => (Decode("boom", 19, 275).Kind, Decode("boom", 19, 275).ConstraintName).ShouldBe((AlvoConstraintKind.Check, null));

    /// <summary>The engine reports a restricting action's foreign-key failure under the trigger code.</summary>
    [Fact]
    public void A_trigger_code_carrying_a_foreign_key_message_is_a_referenced_refusal()
        => Decode("FOREIGN KEY constraint failed", 19, 1811).Kind.ShouldBe(AlvoConstraintKind.Referenced);

    [Fact]
    public void A_trigger_code_carrying_an_untagged_message_is_not_decoded()
        => Dialect.DecodeConstraintViolation(new SqliteException("nope", 19, 1811)).ShouldBeNull();

    [Theory]
    [InlineData(1811)]
    [InlineData(19)]
    public void An_exclusion_tag_decodes_under_the_trigger_code_and_the_bare_code(int extended)
        => Decode($"SQLite Error 19: '{ExclusionTag}xc_B9_a'.", 19, extended).ShouldSatisfyAllConditions(
            decoded => decoded.Kind.ShouldBe(AlvoConstraintKind.Exclusion),
            decoded => decoded.ConstraintName.ShouldBe("xc_B9_a"));

    [Fact]
    public void An_exclusion_tag_with_no_name_decodes_without_one()
    {
        var decoded = Decode($"SQLite Error 19: '{ExclusionTag}'.", 19, 1811);

        (decoded.Kind, decoded.ConstraintName).ShouldBe((AlvoConstraintKind.Exclusion, null));
    }

    [Theory]
    [InlineData("FOREIGN KEY constraint failed")]
    [InlineData("UNIQUE constraint failed: t.a")]
    [InlineData("CHECK constraint failed: ck_a")]
    [InlineData("alvo_exclusion:xc_a")]
    public void A_message_is_not_read_under_a_code_that_is_not_a_constraint(string message)
        => Dialect.DecodeConstraintViolation(new SqliteException(message, 1)).ShouldBeNull();

    [Theory]
    [InlineData(1, "non-deterministic use of unixepoch() in an index", true)]
    [InlineData(1, "non-deterministic use of unixepoch() in a view", false)]
    [InlineData(1, "an error in an index", false)]
    [InlineData(19, "non-deterministic use of unixepoch() in an index", false)]
    public void An_index_predicate_refusal_needs_the_generic_code_and_both_halves_of_sqlites_text(int code, string message, bool expected)
        => Dialect.IsIndexPredicateRefusal(new SqliteException(message, code)).ShouldBe(expected);

    [Fact]
    public void A_failure_that_is_not_sqlites_is_not_an_index_predicate_refusal()
        => Dialect.IsIndexPredicateRefusal(new NotSqliteException()).ShouldBeFalse();

    /// <summary>The verify query is executable and names a row left orphaned, scoped to the touched tables and their referrers.</summary>
    [Theory]
    [InlineData("[\"child\"]", 1)]
    [InlineData("[\"parent\"]", 1)]
    [InlineData("[\"unrelated\"]", 0)]
    [InlineData(null, 1)]
    public void The_framing_verify_reports_an_orphan_inside_its_scope(string? touched, int orphans)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id)); CREATE TABLE unrelated (id INTEGER);");
        Execute(connection, "PRAGMA foreign_keys = 0; INSERT INTO child VALUES (1, 99);");
        using var command = connection.CreateCommand();
        command.CommandText = _concrete.MigrationFraming.Verify;
        command.Parameters.AddWithValue("@touched", (object?)touched ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        var rows = new List<(string Table, string From)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(3)));
        }

        rows.ShouldBe(orphans == 0 ? [] : [("child", "parent_id")]);
    }

    private SqlConstraintViolation Decode(string message, int code, int extended)
        => Dialect.DecodeConstraintViolation(new SqliteException(message, code, extended)).ShouldNotBeNull();

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A database failure that is not SQLite's, to prove the decode answers only for its own driver's exceptions.</summary>
    private sealed class NotSqliteException : System.Data.Common.DbException
    {
    }
}
