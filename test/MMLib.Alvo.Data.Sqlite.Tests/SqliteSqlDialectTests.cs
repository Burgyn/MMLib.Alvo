using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public class SqliteSqlDialectTests
{
    private static readonly SqliteSqlDialect _dialect = new();

    /// <summary>
    /// The whole documented grammar in one assertion: a bare quoted table source, no surrounding
    /// parentheses, no alias, no <c>FROM</c> keyword, no terminator, nothing to trim.
    /// </summary>
    [Fact]
    public void A_table_is_a_bare_quoted_name_with_no_alias_and_no_from_keyword()
        => _dialect.RenderTable(Entity("vehicle")).ShouldBe("\"vehicle\"");

    [Fact]
    public void A_null_projection_is_a_bare_expression_with_no_column_alias()
        => _dialect.RenderNullProjection("TEXT").ShouldBe("CAST(NULL AS TEXT)");

    [Fact]
    public void A_column_is_a_bare_quoted_reference_with_no_table_qualifier_and_no_alias()
        => _dialect.RenderColumn("secret_note").ShouldBe("\"secret_note\"");

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    [Fact]
    public void A_parameterised_store_type_reaches_the_cast_unrewritten()
        => _dialect.RenderNullProjection("varchar(32)").ShouldBe("CAST(NULL AS varchar(32))");

    [Fact]
    public void A_null_projection_refuses_a_missing_store_type_rather_than_casting_to_nothing()
        => Should.Throw<ArgumentException>(() => _dialect.RenderNullProjection("  "));

    /// <summary>
    /// SQLite has no row-locking clause at all, and the empty string is how a dialect says so. It must be
    /// genuinely empty rather than whitespace: a composer that only checks for <c>""</c> would otherwise
    /// emit a stray separator, and one that checks <c>IsNullOrWhiteSpace</c> would mask the difference.
    /// </summary>
    [Fact]
    public void There_is_no_row_lock_clause()
        => _dialect.RowLockHint.ShouldBe(string.Empty);

    [Fact]
    public void The_row_lock_hint_carries_no_separator_of_its_own()
        => _dialect.RowLockHint.ShouldBe(_dialect.RowLockHint.Trim());
}
