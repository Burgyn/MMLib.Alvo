using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The text built-ins of spec §5.1, each against the edge cases C2's SQL must reproduce.</summary>
public sealed class CelTextBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, AlvoContext.Anonymous);

    [Theory]
    [InlineData("héllo", 1L, 3L, "él")]
    [InlineData("😀ab", 1L, 3L, "ab")]
    [InlineData("abc", 0L, 3L, "abc")]
    [InlineData("abc", 3L, 3L, "")]
    [InlineData("abc", 1L, 1L, "")]
    public void Substring_cuts_code_points_start_inclusive_end_exclusive(string text, long start, long end, string expected) =>
        Mutate($"substring(name, {start}, {end})", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("😀ab", 1L, "ab")]
    [InlineData("abc", 3L, "")]
    public void Substring_without_an_end_runs_to_the_end(string text, long start, string expected) =>
        Mutate($"substring(name, {start})", ("name", text)).ShouldBe(expected);

    /// <summary>
    /// A fact, not a theory: xUnit serialises <c>InlineData</c> strings, and a lone surrogate does not survive the round
    /// trip, so the rows would test U+FFFD instead.
    /// </summary>
    [Fact]
    public void Substring_keeps_a_lone_surrogate_as_it_was()
    {
        CelBuiltInFunctions.SubstringText("a\ud800b", 1, 2).ShouldBe("\ud800");
        CelBuiltInFunctions.SubstringText("a\ud800", 1, end: null).ShouldBe("\ud800", "a lone high surrogate at the very end");
        CelBuiltInFunctions.SubstringText("\udc00b", 0, 1).ShouldBe("\udc00", "a lone low surrogate");
    }

    [Theory]
    [InlineData(-1L, 2L)]
    [InlineData(2L, 1L)]
    [InlineData(0L, 4L)]
    [InlineData(4L, 4L)]
    public void Substring_outside_the_text_fails_closed_without_naming_a_position(long start, long end)
    {
        var failure = Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.SubstringText("abc", start, end));

        failure.FunctionName.ShouldBe("substring");
        failure.Reason.ShouldBe("a position is outside the text");
    }

    [Theory]
    [InlineData("a😀b", long.MaxValue, null)]
    [InlineData("a😀b", 1L, long.MaxValue)]
    [InlineData("a😀b", 1L, 4L)]
    [InlineData("😀", 2L, null)]
    public void Substring_far_past_the_text_fails_closed_without_walking_further(string text, long start, long? end) =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.SubstringText(text, start, end))
            .Reason.ShouldBe("a position is outside the text");

    [Theory]
    [InlineData("😀😀", 2L, 2L, "")]
    [InlineData("😀😀", 1L, null, "😀")]
    [InlineData("a😀b", 3L, null, "")]
    public void Substring_at_the_very_end_of_a_text_is_empty_or_its_tail(string text, long start, long? end, string expected) =>
        CelBuiltInFunctions.SubstringText(text, start, end).ShouldBe(expected);

    [Fact]
    public void Substring_without_an_end_past_the_text_fails_closed() =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.SubstringText("abc", 4, end: null))
            .Reason.ShouldBe("a position is outside the text");

    /// <summary>
    /// The security-core half: a function failure inside a hook condition escapes the evaluator rather than collapsing
    /// to <c>false</c> (which would let a reject through) — and no negation or comparison around it can turn it back
    /// into an answer.
    /// </summary>
    [Theory]
    [InlineData("substring(new.name, 0, 9) == 'x'")]
    [InlineData("!(substring(new.name, 5) == 'x')")]
    [InlineData("startsWith(substring(new.name, 2, 1), 'x')")]
    public void A_substring_failure_in_a_condition_fails_closed_rather_than_answering(string source) =>
        Should.Throw<CelFunctionException>(() => Condition(source, ("name", "abc"))).FunctionName.ShouldBe("substring");

    [Fact]
    public void A_substring_failure_in_a_mutate_value_fails_closed_rather_than_writing_null() =>
        Should.Throw<CelFunctionException>(() => Mutate("substring(name, 0, 9)", ("name", "abc"))).FunctionName.ShouldBe("substring");

    [Fact]
    public void The_truncation_recipe_cuts_only_what_is_too_long()
    {
        const string recipe = "substring(name, 0, math.least(size(name), 5))";

        Mutate(recipe, ("name", "abcdefgh")).ShouldBe("abcde");
        Mutate(recipe, ("name", "abc")).ShouldBe("abc");
    }

    [Theory]
    [InlineData("contains(new.name, 'b')", "abc", true)]
    [InlineData("contains(new.name, 'B')", "abc", false)]
    [InlineData("contains(new.name, '')", "abc", true)]
    [InlineData("startsWith(new.name, 'ab')", "abc", true)]
    [InlineData("startsWith(new.name, '')", "", true)]
    [InlineData("startsWith(new.name, 'e')", "é", true)]
    [InlineData("startsWith(new.name, 'e')", "é", false)]
    [InlineData("endsWith(new.name, '@kros.sk')", "a@kros.sk", true)]
    [InlineData("endsWith(new.name, '@KROS.SK')", "a@kros.sk", false)]
    [InlineData("endsWith(lowerAscii(new.name), '@kros.sk')", "A@KROS.SK", true)]
    public void A_text_test_is_ordinal_and_an_empty_search_matches(string source, string name, bool expected) =>
        Condition(source, ("name", name)).ShouldBe(expected);

    [Theory]
    [InlineData("contains(new.name, 'x')")]
    [InlineData("startsWith(new.name, 'x')")]
    [InlineData("endsWith(new.name, 'x')")]
    public void A_text_test_over_an_empty_field_does_not_fire(string source) =>
        Condition(source, ("name", null)).ShouldBeFalse("null in, null out; a condition reading null does not fire");

    /// <summary>
    /// The other half, and the reason the guided form offers no negated text tests (spec §10, D-2): '!' applies to the
    /// already-collapsed boolean (<c>CelInterpreter.AsBoolean</c>), so the negation of an empty test is <c>true</c>.
    /// </summary>
    [Fact]
    public void A_negated_text_test_over_an_empty_field_fires() =>
        Condition("!endsWith(new.name, 'x')", ("name", null)).ShouldBeTrue("!null collapses to !false, which is true");

    /// <summary>
    /// Pins today's behaviour, not a promise: the text tests compare UTF-16 code units ordinally, while
    /// <c>size</c> and <c>substring</c> count code points. A lone surrogate therefore matches half of a pair.
    /// </summary>
    [Theory]
    [InlineData("contains(new.name, new.high)", "😀x")]
    [InlineData("startsWith(new.name, new.high)", "😀x")]
    [InlineData("endsWith(new.name, new.low)", "x😀")]
    public void A_text_test_compares_utf16_units_so_a_lone_surrogate_matches_half_a_pair(string source, string name)
    {
        var halves = new EntitySchema
        {
            Name = "halves",
            Fields =
            [
                new FieldSchema { Name = "name", Type = FieldType.String, MaxLength = 20, Nullable = true },
                new FieldSchema { Name = "high", Type = FieldType.String, MaxLength = 20, Nullable = true },
                new FieldSchema { Name = "low", Type = FieldType.String, MaxLength = 20, Nullable = true },
            ],
        };
        var compiled = TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, halves).Expression!;

        CelInterpreter.EvaluatePredicate(
            compiled, CelFixtures.Row(("name", name), ("high", "\ud83d"), ("low", "\ude00")), previous: null, AlvoContext.Anonymous)
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("contains(new.name, 'x')", CelProfile.Condition, "Bool")]
    [InlineData("substring(name, 1)", CelProfile.Mutate, "String")]
    [InlineData("substring(name, 1, 2)", CelProfile.Mutate, "String")]
    public void Each_text_built_in_has_its_result_type(string source, CelProfile profile, string expected) =>
        TestCelFunctions.Compile(source, profile).ResultType.ToString().ShouldBe(expected);

    [Theory]
    [InlineData("substring(name, 1) == 'x'", "substring", CelProfile.Rule)]
    [InlineData("contains(name, 'x')", "contains", CelProfile.Computed)]
    [InlineData("startsWith(name, 'x')", "startsWith", CelProfile.Access)]
    public void A_text_built_in_is_refused_where_sql_renders(string source, string name, CelProfile profile)
    {
        var refused = TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldStartWith($"'{name}(...)' is not available in the {profile} profile");
    }
}
