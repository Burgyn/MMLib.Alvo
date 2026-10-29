namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>A literal text value as a <c>LIKE</c> pattern: every character that means something to <c>LIKE</c> is escaped.</summary>
public sealed class LiteralTextPatternTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("C:\\tmp", "C:\\\\tmp")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("%_\\[", "\\%\\_\\\\\\[")]
    [InlineData("", "")]
    public void Every_pattern_character_is_escaped_and_nothing_else(string value, string escaped)
        => Internal.LiteralTextPattern.Escaped(value).ShouldBe(escaped);

    [Theory]
    [InlineData(AlvoFilterOperator.StartsWith, "a%", "a\\%%")]
    [InlineData(AlvoFilterOperator.EndsWith, "a%", "%a\\%")]
    [InlineData(AlvoFilterOperator.Contains, "a%", "%a\\%%")]
    public void Each_operator_places_the_wildcard_on_its_open_side(AlvoFilterOperator op, string value, string pattern)
        => Internal.LiteralTextPattern.For(op, value).ShouldBe(pattern);

    [Fact]
    public void Only_the_three_literal_operators_are_literal_text_tests()
        => Enum.GetValues<AlvoFilterOperator>().Where(Internal.LiteralTextPattern.IsLiteralTextTest)
            .ShouldBe([AlvoFilterOperator.StartsWith, AlvoFilterOperator.EndsWith, AlvoFilterOperator.Contains]);
}
