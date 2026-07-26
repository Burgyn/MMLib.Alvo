using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

public class PostgreSqlSqlDialectTests
{
    private static readonly PostgreSqlSqlDialect _dialect = new();

    /// <summary>
    /// The whole documented grammar in one assertion: a bare quoted table source, no surrounding
    /// parentheses, no alias, no <c>FROM</c> keyword, no terminator, nothing to trim. In particular no
    /// database schema qualifier — <c>AlvoOptions.SchemaPrefix</c> is a table-name prefix, not a schema.
    /// </summary>
    [Fact]
    public void A_table_is_a_bare_quoted_name_with_no_alias_and_no_from_keyword()
        => _dialect.RenderTable(Entity("vehicle")).ShouldBe("\"vehicle\"");

    [Fact]
    public void A_null_projection_is_a_bare_expression_with_no_column_alias()
        => _dialect.RenderNullProjection("text").ShouldBe("CAST(NULL AS text)");

    [Fact]
    public void A_column_is_a_bare_quoted_reference_with_no_table_qualifier_and_no_alias()
        => _dialect.RenderColumn("secret_note").ShouldBe("\"secret_note\"");

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    /// <summary>
    /// The regression this signature exists for: the dialect used to derive the type from a
    /// <c>FieldSchema</c> and answered <c>numeric(18,2)</c> for every decimal, <c>jsonb</c> for a
    /// <c>json</c> column the migrator actually creates as <c>text</c>, and <c>text</c> for a
    /// length-bounded string the migrator creates as <c>character varying(N)</c>.
    /// </summary>
    [Theory]
    [InlineData("numeric(10,4)")]
    [InlineData("character varying(32)")]
    [InlineData("timestamp with time zone")]
    public void A_parameterised_store_type_reaches_the_cast_unrewritten(string storeType)
        => _dialect.RenderNullProjection(storeType).ShouldBe($"CAST(NULL AS {storeType})");

    [Fact]
    public void A_null_projection_refuses_a_missing_store_type_rather_than_casting_to_nothing()
        => Should.Throw<ArgumentException>(() => _dialect.RenderNullProjection("  "));

    /// <summary>
    /// <c>FOR NO KEY UPDATE</c>, not <c>FOR UPDATE</c>: the pre-image read never precedes a key change,
    /// and the weaker mode does not block a concurrent inserter's foreign-key check against this row.
    /// </summary>
    [Fact]
    public void The_row_lock_is_the_no_key_variant()
        => _dialect.RowLockHint.ShouldBe("FOR NO KEY UPDATE");

    /// <summary>
    /// The hint carries no separator of its own — the composer inserts the space. A value that shipped its
    /// own leading space would concatenate correctly at a composer written for the other convention and
    /// produce <c>… WHERE &lt;predicate&gt;  FOR NO KEY UPDATE</c> or, the other way round,
    /// <c>&lt;predicate&gt;FOR NO KEY UPDATE</c>.
    /// </summary>
    [Fact]
    public void The_row_lock_hint_carries_no_separator_of_its_own()
        => _dialect.RowLockHint.ShouldBe(_dialect.RowLockHint.Trim());
}
