namespace MMLib.Alvo.Data.Sqlite.Tests;

public class SqliteSqlDialectTests
{
    private static readonly SqliteSqlDialect _dialect = new();

    [Fact]
    public void A_null_projection_casts_to_the_store_type_it_was_given()
        => _dialect.RenderNullProjection("TEXT").ShouldBe("CAST(NULL AS TEXT)");

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
