namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>What the graders read of a reply or an expression.</summary>
public sealed class ReplyTextTests
{
    [Fact]
    public void Own_prose_drops_quote_lines_and_repeated_framework_text() =>
        ReplyText.OwnProse("> quoted refusal\nAlvo said: No way. — Try later.\nMine.", ["No way.", "Try later."])
            .ShouldBe("Alvo said:  — \nMine.");

    [Theory]
    [InlineData("  First one. Second one.", "First one.")]
    [InlineData("Line one\nline two.", "Line one")]
    [InlineData("Version 1.2 is out! Next.", "Version 1.2 is out!")]
    [InlineData("No end", "No end")]
    public void The_first_sentence_ends_at_a_sentence_end_or_a_line_break(string prose, string first) =>
        ReplyText.FirstSentence(prose).ShouldBe(first);

    [Theory]
    [InlineData("has( middle_name ) ? ' ' + middle_name : ''", "has(middle_name)?' '+middle_name:''")]
    [InlineData("'technician'  in  @user.roles", "'technician'in@user.roles")]
    [InlineData("a + \" x \"", "a+\" x \"")]
    public void Compact_removes_layout_but_keeps_the_text_inside_literals(string expression, string compact) =>
        ReplyText.Compact(expression).ShouldBe(compact);
}
