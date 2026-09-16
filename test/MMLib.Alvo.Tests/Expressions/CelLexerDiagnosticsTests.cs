using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// What a token carries beyond its kind — the lexeme, the terminating marker's position — and what
/// each lexer refusal has to name: the offending character, escape, context reference or operator.
/// The prose around those substrings is deliberately not asserted.
/// </summary>
public class CelLexerDiagnosticsTests
{
    private static CelToken[] Tokens(string source) => [.. CelLexer.Tokenize(source)];

    /// <summary>
    /// The stream ends with a zero-width marker at the end of the source: it is the token the parser
    /// reports a position from when input runs out mid-expression.
    /// </summary>
    [Fact]
    public void The_stream_ends_with_an_empty_end_of_input_token_at_the_source_end()
    {
        var source = "owner_id == 1";

        var last = Tokens(source)[^1];

        last.Kind.ShouldBe(CelTokenKind.EndOfInput);
        last.Text.ShouldBeEmpty();
        last.Position.ShouldBe(source.Length);
    }

    /// <summary>
    /// A token carries the exact source lexeme it was read from — including the two-character operators,
    /// whose text the reader supplies rather than slicing it out of the source.
    /// </summary>
    [Theory]
    [InlineData("a == b", "==")]
    [InlineData("a && b", "&&")]
    [InlineData("a || b", "||")]
    [InlineData("a != b", "!=")]
    [InlineData("a <= b", "<=")]
    [InlineData("a >= b", ">=")]
    public void A_two_character_operator_token_carries_its_lexeme(string source, string lexeme)
    {
        Tokens(source)[1].Text.ShouldBe(lexeme);
    }

    /// <summary>The half-written operator is named by the operator it was meant to be, since doubling the character is the fix.</summary>
    [Theory]
    [InlineData("a & b", "&&")]
    [InlineData("a | b", "||")]
    [InlineData("a = b", "==")]
    public void A_single_character_where_a_doubled_operator_belongs_names_that_operator(string source, string expected)
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize(source))
            .Message.ShouldContain(expected);
    }

    [Fact]
    public void An_unknown_context_reference_is_named_in_the_message()
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("@session.id"))
            .Message.ShouldContain("@session");
    }

    [Fact]
    public void An_unexpected_character_is_named_in_the_message()
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("owner_id # 1"))
            .Message.ShouldContain("#");
    }

    /// <summary>
    /// A digit run is a decimal only when the dot is followed by another digit. Every other dot placement
    /// is a malformed literal — and a <see cref="CelSyntaxException"/>, never an index fault off the end
    /// of the source, which is the promise the lexer makes about hostile input.
    /// </summary>
    [Theory]
    [InlineData("1.")]
    [InlineData("1.x")]
    [InlineData("1.2.3")]
    public void A_dot_not_followed_by_a_digit_is_a_malformed_numeric_literal(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize(source))
            .Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void An_unknown_escape_sequence_names_the_offending_escape()
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("'a\\q'"))
            .Message.ShouldContain("\\q");
    }

    /// <summary>A backslash as the last character of the source is a refusal with a diagnostic, not an index fault.</summary>
    [Fact]
    public void An_unterminated_escape_sequence_is_refused_with_a_diagnostic()
    {
        Should.Throw<CelSyntaxException>(() => CelLexer.Tokenize("'abc\\"))
            .Message.ShouldNotBeNullOrEmpty();
    }
}
