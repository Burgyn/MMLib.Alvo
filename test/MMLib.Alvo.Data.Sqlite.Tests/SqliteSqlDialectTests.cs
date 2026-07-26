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
}
