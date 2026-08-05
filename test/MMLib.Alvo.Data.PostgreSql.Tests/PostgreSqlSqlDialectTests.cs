using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// PostgreSQL's leg of the SQL-seam contract, plus the answers that are this engine's own — including the one
/// place the two shipped drivers genuinely differ, the row-locking clause and its two modes.
/// </summary>
public class PostgreSqlSqlDialectTests : AlvoSqlDialectContractTests
{
    private static readonly PostgreSqlSqlDialect _dialect = new();

    protected override IAlvoSqlDialect CreateDialect() => _dialect;

    protected override IFieldSqlRenderer CreateFieldRenderer() => new PostgreSqlFieldSqlRenderer();

    /// <summary>
    /// In particular no database schema qualifier — <c>AlvoOptions.SchemaPrefix</c> is a table-name prefix, not
    /// a schema.
    /// </summary>
    [Fact]
    public void A_table_is_a_bare_quoted_name_with_no_schema_qualifier()
        => _dialect.RenderTable(Entity("vehicle"), lockedPreImageFor: null).ShouldBe("\"vehicle\"");

    [Fact]
    public void A_null_projection_is_a_standard_cast()
        => _dialect.RenderNullProjection("text").ShouldBe("CAST(NULL AS text)");

    [Fact]
    public void A_column_is_a_bare_quoted_reference()
        => _dialect.RenderColumn("secret_note").ShouldBe("\"secret_note\"");

    /// <summary>
    /// Spike Q8, and the reason this dialect does not delegate to Npgsql's own
    /// <c>ISqlGenerationHelper.DelimitIdentifier</c>: that helper returns <c>plate</c> unquoted, which
    /// PostgreSQL case-folds, so the same field would render differently per driver.
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

    /// <summary>
    /// <c>FOR NO KEY UPDATE</c>, not <c>FOR UPDATE</c>: an update's pre-image read never precedes a key
    /// change, and the weaker mode does not block a concurrent inserter's foreign-key check against this
    /// row.
    /// </summary>
    [Fact]
    public void An_updates_pre_image_takes_the_weaker_no_key_lock()
        => _dialect.RowLockClause(PreImageMutation.Update).ShouldBe("FOR NO KEY UPDATE");

    /// <summary>
    /// A delete removes the row's key, so it needs the stronger mode — and <c>FOR NO KEY UPDATE</c> is
    /// defined as the one that declines to block <c>FOR KEY SHARE</c>, which is exactly the lock a
    /// concurrent foreign-key check takes on the row this delete is about to remove.
    /// </summary>
    [Fact]
    public void A_deletes_pre_image_takes_the_full_row_lock()
        => _dialect.RowLockClause(PreImageMutation.Delete).ShouldBe("FOR UPDATE");

    /// <summary>The two mutations must not share a mode, or the distinction would be decorative.</summary>
    [Fact]
    public void The_two_mutations_take_different_modes()
        => _dialect.RowLockClause(PreImageMutation.Update)
            .ShouldNotBe(_dialect.RowLockClause(PreImageMutation.Delete));

    /// <summary>
    /// This engine's locking grammar is the trailing clause, so the table source must be untouched: hinting it
    /// as well would be locking twice, which the port forbids and which PostgreSQL has no syntax for anyway.
    /// </summary>
    [Theory]
    [InlineData(PreImageMutation.Update)]
    [InlineData(PreImageMutation.Delete)]
    public void A_pre_image_reads_the_same_table_source_as_an_ordinary_read(PreImageMutation mutation)
        => _dialect.RenderTable(Entity("vehicle"), mutation)
            .ShouldBe(_dialect.RenderTable(Entity("vehicle"), lockedPreImageFor: null));

    /// <summary>
    /// The exact spelling, pinned per engine — PostgreSQL's own since 12, and it names the type, unlike
    /// T-SQL's <c>PERSISTED</c> form.
    /// </summary>
    [Fact]
    public void A_stored_generated_column_is_spelled_generated_always_as_stored()
        => _dialect.GeneratedColumnDefinition("line_total", "numeric(18,2)", "\"unit_price\" * \"amount\"")
            .ShouldBe("\"line_total\" numeric(18,2) GENERATED ALWAYS AS (\"unit_price\" * \"amount\") STORED");
}
