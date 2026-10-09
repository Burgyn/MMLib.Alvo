using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The lexer's progress invariant (#244): a step that consumed no character is refused with a diagnostic, instead of
/// re-reading the same character forever and growing the token list without bound.
/// </summary>
public sealed class CelLexerProgressTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(7, 6)]
    public void A_step_that_consumed_nothing_is_refused_at_its_position(int start, int end) =>
        Should.Throw<CelSyntaxException>(() => CelLexer.EnsureAdvanced(start, end)).Position.ShouldBe(start);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 9)]
    public void A_step_that_consumed_a_character_passes(int start, int end) =>
        Should.NotThrow(() => CelLexer.EnsureAdvanced(start, end));

    [Theory]
    [InlineData("title")]
    [InlineData("'x'")]
    [InlineData("12")]
    [InlineData("1.5")]
    [InlineData("@user")]
    [InlineData("true")]
    [InlineData("(")]
    [InlineData(",")]
    [InlineData("==")]
    [InlineData("<=")]
    [InlineData("!")]
    [InlineData("&&")]
    public void Every_token_family_advances_to_the_end_of_input(string source)
    {
        var tokens = CelLexer.Tokenize(source);

        tokens.Count.ShouldBe(2);
        tokens[^1].Kind.ShouldBe(CelTokenKind.EndOfInput);
        tokens[^1].Position.ShouldBe(source.Length);
    }
}
