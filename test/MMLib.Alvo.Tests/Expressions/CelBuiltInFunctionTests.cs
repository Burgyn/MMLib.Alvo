using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The five built-ins, each against the edge cases spec §6 pins (and C2's SQL must reproduce).</summary>
public sealed class CelBuiltInFunctionTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Evaluate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData("a-b-c", "-", "+", "a+b+c")]
    [InlineData("aaa", "aa", "b", "ba")]
    [InlineData("abc", "", "x", "abc")]
    [InlineData("abc", "z", "x", "abc")]
    [InlineData("ABC", "b", "x", "ABC")]
    [InlineData("", "a", "b", "")]
    public void Replace_is_ordinal_left_to_right_and_an_empty_search_changes_nothing(string text, string search, string replacement, string expected) =>
        Evaluate($"replace(name, '{search}', '{replacement}')", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("\u00e9", "e\u0301")]
    [InlineData("e\u0301", "\u00e9")]
    [InlineData("a\u00adb", "ab")]
    [InlineData("\u0130", "i")]
    public void Replace_compares_code_units_and_never_culture_or_normalisation(string text, string search)
    {
        text.ShouldNotBe(search);

        CelBuiltInFunctions.ReplaceText(text, search, "X").ShouldBe(text);
        Evaluate($"replace(name, '{search}', 'X')", ("name", text)).ShouldBe(text);
    }

    [Theory]
    [InlineData("  a b  ", "a b")]
    [InlineData("\t\n\r a \r\n\t", "a")]
    [InlineData("\u00a0a\u00a0", "\u00a0a\u00a0")]
    [InlineData("\u2003a", "\u2003a")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Trim_removes_the_four_ascii_whitespace_characters_and_nothing_else(string text, string expected) =>
        Evaluate("trim(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("", 0L)]
    [InlineData("abc", 3L)]
    [InlineData("\ud83d\ude00", 1L)]
    [InlineData("e\u0301", 2L)]
    public void Size_counts_unicode_code_points(string text, long expected) =>
        Evaluate("size(name)", ("name", text)).ShouldBe(expected);

    [Fact]
    public void Size_counts_a_lone_surrogate_as_one() =>
        Evaluate("size(name)", ("name", "a\ud800")).ShouldBe(2L);

    [Theory]
    [InlineData(-5L, 5L)]
    [InlineData(5L, 5L)]
    [InlineData(0L, 0L)]
    public void Abs_of_an_int_is_an_int(long value, long expected) =>
        Evaluate("math.abs(qty)", ("qty", value)).ShouldBe(expected);

    [Fact]
    public void Abs_of_a_decimal_is_a_decimal() =>
        Evaluate("math.abs(price)", ("price", -2.50m)).ShouldBe(2.50m);

    [Fact]
    public void Abs_of_the_smallest_int_fails_closed_with_a_reason()
    {
        var failure = Should.Throw<CelFunctionException>(() => Evaluate("math.abs(qty)", ("qty", long.MinValue)));

        failure.FunctionName.ShouldBe("math.abs");
        failure.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("2.5", "3")]
    [InlineData("-2.5", "-3")]
    [InlineData("0.5", "1")]
    [InlineData("-0.5", "-1")]
    [InlineData("1.4999", "1")]
    [InlineData("2.4", "2")]
    public void Round_takes_halves_away_from_zero(string value, string expected) =>
        Evaluate("math.round(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture)))
            .ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));

    [Fact]
    public void Round_of_an_int_is_the_int() => Evaluate("math.round(qty)", ("qty", 7L)).ShouldBe(7L);

    [Theory]
    [InlineData("trim(name)")]
    [InlineData("size(name)")]
    [InlineData("replace(name, 'a', 'b')")]
    [InlineData("math.abs(qty)")]
    [InlineData("math.round(price)")]
    public void A_null_argument_makes_every_built_in_null(string source) => Evaluate(source).ShouldBeNull();

    [Fact]
    public void A_replace_that_would_grow_past_the_cap_fails_closed()
    {
        var source = $"replace(name, 'a', '{new string('b', 1100)}')";

        Should.Throw<CelFunctionException>(() => Evaluate(source, ("name", new string('a', 1000)))).FunctionName.ShouldBe("replace");
    }

    [Fact]
    public void A_replace_that_shrinks_a_text_longer_than_the_cap_is_not_capped() =>
        Evaluate("replace(name, 'a', '')", ("name", new string('a', CelBuiltInFunctions.MaxTextLength + 1))).ShouldBe(string.Empty);

    [Fact]
    public void A_replace_growing_to_exactly_the_cap_is_allowed_and_one_over_is_refused()
    {
        var half = new string('a', CelBuiltInFunctions.MaxTextLength / 2);

        CelBuiltInFunctions.ReplaceText(half, "a", "aa").Length.ShouldBe(CelBuiltInFunctions.MaxTextLength);
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.ReplaceText(half + "b", "a", "aa")).FunctionName.ShouldBe("replace");
    }

    [Fact]
    public void A_replace_over_the_cap_is_judged_by_growth_not_by_the_replacement_alone()
    {
        var overCap = new string('a', CelBuiltInFunctions.MaxTextLength + 10);

        CelBuiltInFunctions.ReplaceText(overCap, "aa", "b").Length.ShouldBe((CelBuiltInFunctions.MaxTextLength + 10) / 2);
        CelBuiltInFunctions.ReplaceText(overCap, "a", "b").Length.ShouldBe(overCap.Length);
    }

    [Fact]
    public void Built_ins_compose_in_a_mutate() =>
        Evaluate("trim(replace(name, '-', ' '))", ("name", "-a-b-")).ShouldBe("a b");

    [Fact]
    public void A_built_in_works_in_a_hook_condition() =>
        CelInterpreter.EvaluatePredicate(
            TestCelFunctions.Compile("size(name) > 3", CelProfile.Condition), CelFixtures.Row(("name", "abcd")), null, CelFixtures.Alice)
            .ShouldBeTrue();

    [Fact]
    public void A_built_in_is_refused_in_a_rule_in_this_slice() =>
        new CelCompiler().Compile("trim(name) == 'x'", CelProfile.Rule, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldStartWith("'trim(...)' is not available in the Rule profile");

    [Fact]
    public void A_field_named_like_a_built_in_is_still_a_field()
    {
        Evaluate("trim(round)", ("round", " x ")).ShouldBe("x");
        Evaluate("size(size)", ("size", "abc")).ShouldBe(3L);
    }

    [Fact]
    public void Every_built_in_body_returns_exactly_its_declared_clr_type()
    {
        // Int is a position inside the String sample, so substring's body answers rather than fails closed.
        var samples = new Dictionary<CelValueType, object> { [CelValueType.Int] = 1L, [CelValueType.Decimal] = -3.5m, [CelValueType.String] = " a " };

        foreach (var function in CelBuiltInFunctions.All.Where(f => !f.IsLegacy))
        {
            var arguments = function.Parameters.Select(p => samples[p.Type]).ToArray();
            var result = function.Body!(arguments);

            result.ShouldBeOfType(CelBuiltInFunctions.ClrTypeOf(function.ResultType), function.Signature());
        }
    }

    [Fact]
    public void A_catalog_refuses_one_name_with_mixed_provenance_or_profiles()
    {
        var builtIn = CelBuiltInFunctions.All.First(f => f.Name == "trim");

        Should.Throw<InvalidOperationException>(() => new CelFunctionCatalog([builtIn, builtIn with { IsHost = true }]));
        Should.Throw<InvalidOperationException>(() => new CelFunctionCatalog([builtIn, builtIn with { Profiles = CelBuiltInFunctions.MutateOnly }]));
    }

    [Fact]
    public void A_built_in_profile_set_is_a_fresh_copy_each_time()
    {
        var first = (HashSet<CelProfile>)CelBuiltInFunctions.ConditionAndMutate;
        first.Add(CelProfile.Rule);

        CelBuiltInFunctions.ConditionAndMutate.Contains(CelProfile.Rule).ShouldBeFalse();
        CelBuiltInFunctions.All.First(f => f.Name == "size").Profiles.Contains(CelProfile.Rule).ShouldBeFalse();
    }
}
