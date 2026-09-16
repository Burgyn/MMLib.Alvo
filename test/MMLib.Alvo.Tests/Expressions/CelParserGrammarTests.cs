using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The grammar facts <see cref="CelParserTests"/> leaves implicit: the source-length and nesting
/// boundaries, left associativity inside one precedence level, and what each refusal has to carry —
/// the offending token, function or literal an agent acts on, never the sentence it is phrased in.
/// </summary>
public class CelParserGrammarTests
{
    private static string MaxSourceLengthText => CelParser.MaxSourceLength.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void A_null_source_is_refused_by_the_argument_guard()
    {
        Should.Throw<ArgumentNullException>(() => CelParser.Parse(null!));
    }

    /// <summary>The limit is inclusive: exactly <c>MaxSourceLength</c> characters is the longest accepted source.</summary>
    [Fact]
    public void A_source_of_exactly_the_maximum_length_is_accepted()
    {
        var source = new string('a', CelParser.MaxSourceLength);

        Should.NotThrow(() => CelParser.Parse(source));
    }

    /// <summary>
    /// The over-length refusal is structured: the limit appears in the fix suggestion too, so an agent
    /// reading only the suggestion still knows what to shorten the condition below.
    /// </summary>
    [Fact]
    public void A_source_one_character_past_the_maximum_names_the_limit_in_its_fix_suggestion()
    {
        var source = new string('a', CelParser.MaxSourceLength + 1);

        var exception = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        exception.FixSuggestion.ShouldNotBeNullOrEmpty();
        exception.FixSuggestion.ShouldContain(MaxSourceLengthText);
    }

    [Theory]
    [InlineData("a + b + c", CelBinaryOperator.Add)]
    [InlineData("a - b - c", CelBinaryOperator.Subtract)]
    [InlineData("a * b * c", CelBinaryOperator.Multiply)]
    [InlineData("a / b / c", CelBinaryOperator.Divide)]
    public void Operators_of_one_precedence_level_associate_to_the_left(string source, CelBinaryOperator expected)
    {
        var root = CelParser.Parse(source).ShouldBeOfType<CelBinary>();

        root.Operator.ShouldBe(expected);
        root.Left.ShouldBeOfType<CelBinary>().Operator.ShouldBe(expected);
    }

    /// <summary>
    /// A chained comparison is diagnosed as non-association, not as trailing input. Without the parser's
    /// own check the stream still fails — at <c>Expect(EndOfInput)</c> — which says nothing about why
    /// <c>a == b == c</c> is refused, and names the wrong thing to fix.
    /// </summary>
    [Fact]
    public void A_chained_comparison_is_diagnosed_as_non_association_not_as_trailing_input()
    {
        var exception = Should.Throw<CelSyntaxException>(() => CelParser.Parse("a == b == c"));

        exception.Message.ShouldNotBeNullOrEmpty();
        exception.Message.ShouldNotContain(nameof(CelTokenKind.EndOfInput));
        exception.Position.ShouldBe(7);
    }

    /// <summary>Ternary chaining counts toward the depth budget even with no parentheses in the source.</summary>
    [Fact]
    public void Ternary_chaining_past_the_maximum_depth_is_refused()
    {
        var source = string.Concat(Enumerable.Repeat("a ? b : ", CelParser.MaxDepth + 1)) + "c";

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }

    [Fact]
    public void Ternary_chaining_at_exactly_the_maximum_depth_is_accepted()
    {
        var source = string.Concat(Enumerable.Repeat("a ? b : ", CelParser.MaxDepth)) + "c";

        Should.NotThrow(() => CelParser.Parse(source));
    }

    /// <summary>
    /// Depth measures nesting, not a running total: each of the three counted productions —
    /// parenthesised group, ternary branch, unary operand — gives its level back once it has been fully
    /// parsed, so an arbitrarily long chain of shallow ones still parses.
    /// </summary>
    [Theory]
    [InlineData("(a == 1)")]
    [InlineData("(a ? b : c)")]
    [InlineData("!a")]
    public void Sequential_nested_productions_do_not_accumulate_depth(string production)
    {
        var source = string.Join(" && ", Enumerable.Repeat(production, CelParser.MaxDepth + 8));

        Should.NotThrow(() => CelParser.Parse(source));
    }

    /// <summary>A call refused for its argument list names the function, so the author knows which one.</summary>
    [Theory]
    [InlineData("has(a, b)", "has()")]
    [InlineData("changed(a, b)", "changed()")]
    [InlineData("lowerAscii(a, b)", "lowerAscii()")]
    [InlineData("now(a)", "now()")]
    public void A_call_with_a_wrong_argument_list_names_the_function(string source, string expected)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .Message.ShouldContain(expected);
    }

    /// <summary><c>now()</c> is a bound instant rather than a value the author picks, so its refusal carries the rewrite.</summary>
    [Fact]
    public void Now_with_an_argument_carries_a_fix_suggestion()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("now(a)"))
            .FixSuggestion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void An_out_of_range_integer_literal_names_the_offending_literal()
    {
        var literal = new string('9', 20);

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(literal))
            .Message.ShouldContain(literal);
    }

    [Fact]
    public void An_out_of_range_decimal_literal_names_the_offending_literal()
    {
        var literal = new string('9', 40) + ".5";

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(literal))
            .Message.ShouldContain(literal);
    }

    [Theory]
    [InlineData("@user.unknown")]
    [InlineData("@tenant.unknown")]
    public void An_unrecognized_context_member_is_named_in_the_message(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .Message.ShouldContain(source);
    }

    [Fact]
    public void An_unrecognized_function_is_named_in_the_message()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("unknown_macro(a)"))
            .Message.ShouldContain("unknown_macro");
    }

    [Fact]
    public void A_token_that_cannot_begin_a_value_is_named_by_kind()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("a == )"))
            .Message.ShouldContain(nameof(CelTokenKind.RightParen));
    }

    /// <summary>
    /// Nested access past <c>old.</c>/<c>new.</c> is diagnosed by the rule that refuses it. With no check
    /// of its own the stream still fails at <c>Expect(EndOfInput)</c>, which hides which narrowing applied.
    /// </summary>
    [Fact]
    public void Nested_access_past_a_state_prefix_is_diagnosed_as_nested_access()
    {
        var exception = Should.Throw<CelSyntaxException>(() => CelParser.Parse("new.a.b"));

        exception.Message.ShouldNotBeNullOrEmpty();
        exception.Message.ShouldNotContain(nameof(CelTokenKind.EndOfInput));
    }

    /// <summary>
    /// Every refusal carries a diagnostic: the compiler turns the message into a structured descriptor
    /// error, and an empty one leaves an agent with nothing to act on.
    /// </summary>
    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("a.b.c")]
    [InlineData("status in ['draft']")]
    public void A_refused_source_carries_a_non_empty_message(string source)
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source))
            .Message.ShouldNotBeNullOrEmpty();
    }
}
