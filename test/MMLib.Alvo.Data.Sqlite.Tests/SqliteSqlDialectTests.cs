using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the SQL-seam contract, plus the answers that are this engine's own. The grammar obligations
/// (nothing to trim, no <c>FROM</c> keyword, no alias, unconditional delimiting, escaping) live in
/// <see cref="AlvoSqlDialectContractTests"/>, so what stays here is what a generic contract cannot state: the
/// exact strings this engine expects.
/// </summary>
public class SqliteSqlDialectTests : AlvoSqlDialectContractTests
{
    private static readonly SqliteSqlDialect _dialect = new();

    protected override IAlvoSqlDialect CreateDialect() => _dialect;

    protected override IFieldSqlRenderer CreateFieldRenderer() => new SqliteFieldSqlRenderer();

    [Fact]
    public void A_table_is_a_bare_quoted_name()
        => _dialect.RenderTable(Entity("vehicle"), lockedPreImageFor: null).ShouldBe("\"vehicle\"");

    [Fact]
    public void A_null_projection_is_a_standard_cast()
        => _dialect.RenderNullProjection("TEXT").ShouldBe("CAST(NULL AS TEXT)");

    [Fact]
    public void A_column_is_a_bare_quoted_reference()
        => _dialect.RenderColumn("secret_note").ShouldBe("\"secret_note\"");

    /// <summary>
    /// Spike Q8: Npgsql's own <c>DelimitIdentifier</c> returns an identifier unquoted when it judges
    /// quoting unnecessary, which the engine then case-folds. Both dialects quote unconditionally so the
    /// same name renders identically on every driver.
    /// </summary>
    [Theory]
    [InlineData("plate")]
    [InlineData("PLATE")]
    [InlineData("select")]
    public void A_name_that_would_not_strictly_need_quoting_is_quoted_anyway(string name)
    {
        _dialect.RenderColumn(name).ShouldBe($"\"{name}\"");
        _dialect.RenderTable(Entity(name), lockedPreImageFor: null).ShouldBe($"\"{name}\"");
    }

    /// <summary>
    /// Proves both members route through <see cref="AlvoSqlIdentifier"/> rather than concatenating their
    /// own quotes: a name carrying a double quote cannot terminate the identifier and reach the statement
    /// as SQL.
    /// </summary>
    [Fact]
    public void A_quote_breaking_name_cannot_escape_the_quoted_identifier()
    {
        _dialect.RenderColumn("a\"; DROP TABLE vehicle; --")
            .ShouldBe("\"a\"\"; DROP TABLE vehicle; --\"");
        _dialect.RenderTable(Entity("a\"; DROP TABLE vehicle; --"), lockedPreImageFor: null)
            .ShouldBe("\"a\"\"; DROP TABLE vehicle; --\"");
    }

    [Fact]
    public void A_missing_column_name_is_refused_rather_than_rendering_empty_quotes()
        => Should.Throw<ArgumentException>(() => _dialect.RenderColumn("  "));

    private static EntitySchema Entity(string name) => new()
    {
        Name = name,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    [Fact]
    public void A_parameterised_store_type_reaches_the_cast_unrewritten()
        => _dialect.RenderNullProjection("varchar(32)").ShouldBe("CAST(NULL AS varchar(32))");

    /// <summary>
    /// SQLite has no row-locking clause at all — for either mutation — and the empty string is how a
    /// dialect says so. It must be genuinely empty rather than whitespace: a composer that only checks
    /// for <c>""</c> would otherwise emit a stray separator, and one that checks
    /// <c>IsNullOrWhiteSpace</c> would mask the difference.
    /// </summary>
    [Theory]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void There_is_no_row_lock_clause_for_either_mutation(PreImageMutation mutation)
        => _dialect.RowLockClause(mutation).ShouldBe(string.Empty);

    /// <summary>
    /// And none in the table source either, which is the honest reading of the empty clause on this engine:
    /// SQLite expresses row locking in neither position, because a write transaction already takes a
    /// database-wide lock. A dialect answering the empty clause <em>and</em> hinting the table source would be
    /// claiming T-SQL's arrangement.
    /// </summary>
    [Theory]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void A_pre_image_reads_the_same_table_source_as_an_ordinary_read(PreImageMutation mutation)
        => _dialect.RenderTable(Entity("vehicle"), mutation)
            .ShouldBe(_dialect.RenderTable(Entity("vehicle"), lockedPreImageFor: null));

    /// <summary>
    /// The exact spelling, pinned per engine because the generic contract suite can only assert that the
    /// column and the expression are present — the surrounding grammar differs by engine, and this is the one
    /// this driver ships. <c>GENERATED ALWAYS</c> is optional in SQLite and spelled anyway, so a reviewer
    /// comparing the two shipped engines' DDL sees one string rather than two dialects of it.
    /// </summary>
    [Fact]
    public void A_stored_generated_column_is_spelled_generated_always_as_stored()
        => _dialect.GeneratedColumnDefinition("line_total", "TEXT", "\"unit_price\" * \"amount\"")
            .ShouldBe("\"line_total\" TEXT GENERATED ALWAYS AS (\"unit_price\" * \"amount\") STORED");
}
