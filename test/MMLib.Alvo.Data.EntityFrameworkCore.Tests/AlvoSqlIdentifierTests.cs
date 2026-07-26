namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class AlvoSqlIdentifierTests
{
    [Fact]
    public void An_ordinary_identifier_is_still_quoted()
        => AlvoSqlIdentifier.Quote("plate").ShouldBe("\"plate\"");

    [Fact]
    public void An_embedded_quote_is_doubled()
        => AlvoSqlIdentifier.Quote("we\"ird").ShouldBe("\"we\"\"ird\"");

    [Fact]
    public void A_quote_breaking_payload_cannot_escape_the_quoted_identifier()
        => AlvoSqlIdentifier.Quote("title\"; DROP TABLE items; --")
            .ShouldBe("\"title\"\"; DROP TABLE items; --\"");

    [Fact]
    public void An_empty_or_whitespace_identifier_is_refused()
        => Should.Throw<ArgumentException>(() => AlvoSqlIdentifier.Quote("  "));
}
