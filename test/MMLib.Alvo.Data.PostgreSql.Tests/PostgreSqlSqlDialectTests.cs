namespace MMLib.Alvo.Data.PostgreSql.Tests;

public class PostgreSqlSqlDialectTests
{
    private static readonly PostgreSqlSqlDialect _dialect = new();

    [Fact]
    public void A_null_projection_casts_to_the_store_type_it_was_given()
        => _dialect.RenderNullProjection("text").ShouldBe("CAST(NULL AS text)");

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
}
