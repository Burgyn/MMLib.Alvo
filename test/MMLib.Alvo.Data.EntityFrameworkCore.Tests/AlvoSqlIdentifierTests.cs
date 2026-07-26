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
    public void A_null_identifier_is_refused()
        => Should.Throw<ArgumentNullException>(() => AlvoSqlIdentifier.Quote(null!));

    [Fact]
    public void An_empty_identifier_is_refused()
        => Should.Throw<ArgumentException>(() => AlvoSqlIdentifier.Quote(string.Empty));

    /// <summary>
    /// The case that distinguishes <c>ThrowIfNullOrWhiteSpace</c> from <c>ThrowIfNullOrEmpty</c>: a
    /// whitespace-only name would otherwise render as <c>"  "</c>, a legal-looking identifier nothing owns.
    /// </summary>
    [Fact]
    public void A_whitespace_only_identifier_is_refused()
        => Should.Throw<ArgumentException>(() => AlvoSqlIdentifier.Quote("  "));
}
