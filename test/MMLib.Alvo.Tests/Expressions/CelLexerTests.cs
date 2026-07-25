using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelLexerTests
{
    private static CelTokenKind[] Kinds(string source) =>
        CelLexer.Tokenize(source).Select(token => token.Kind).ToArray();

    [Fact]
    public void Tokenizes_a_row_field_compared_to_a_context_value()
    {
        Kinds("owner_id == @user.id").ShouldBe(
        [
            CelTokenKind.Identifier,
            CelTokenKind.Equal,
            CelTokenKind.ContextReference,
            CelTokenKind.Dot,
            CelTokenKind.Identifier,
            CelTokenKind.EndOfInput,
        ]);
    }

    [Fact]
    public void Tokenizes_membership_over_a_string_literal()
    {
        Kinds("'editor' in @user.roles").ShouldBe(
        [
            CelTokenKind.StringLiteral,
            CelTokenKind.In,
            CelTokenKind.ContextReference,
            CelTokenKind.Dot,
            CelTokenKind.Identifier,
            CelTokenKind.EndOfInput,
        ]);
    }

    [Theory]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("'a\\nb'", "a\nb")]
    public void Reads_string_literals_with_escapes(string source, string expected)
    {
        CelLexer.Tokenize(source)[0].Text.ShouldBe(expected);
    }

    [Fact]
    public void Doubled_quotes_no_longer_escape_a_quote()
    {
        Kinds("'it''s'").ShouldBe(
        [
            CelTokenKind.StringLiteral,
            CelTokenKind.StringLiteral,
            CelTokenKind.EndOfInput,
        ]);
    }

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("owner_id # 1")]
    [InlineData("@")]
    [InlineData("1.2.3")]
    [InlineData("'a\nb'")]
    public void Refuses_input_it_cannot_tokenize(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize(source));
    }

    [Fact]
    public void Reports_the_position_of_the_offending_character()
    {
        var exception = Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("a && #"));

        exception.Position.ShouldBe(5);
    }
}
