using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The math built-ins of spec §5.2: Decimal stays Decimal, Int stays Int, null in is null out.</summary>
public sealed class CelMathBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("1.2", "2")]
    [InlineData("-1.5", "-1")]
    [InlineData("2.0", "2")]
    public void Ceil_is_the_smallest_whole_number_not_below(string value, string expected) =>
        Mutate("math.ceil(price)", ("price", Number(value))).ShouldBe(Number(expected));

    [Theory]
    [InlineData("1.8", "1")]
    [InlineData("-1.2", "-2")]
    public void Floor_is_the_largest_whole_number_not_above(string value, string expected) =>
        Mutate("math.floor(price)", ("price", Number(value))).ShouldBe(Number(expected));

    [Theory]
    [InlineData("math.ceil(qty)")]
    [InlineData("math.floor(qty)")]
    public void Ceil_and_floor_of_an_int_are_the_int(string source) => Mutate(source, ("qty", 7L)).ShouldBe(7L);

    /// <summary>A Decimal stays a Decimal: ceil and floor never narrow to Int, so a value past Int's range cannot overflow.</summary>
    [Theory]
    [InlineData("math.ceil(price)")]
    [InlineData("math.floor(price)")]
    public void Ceil_and_floor_of_a_decimal_past_int_range_stay_decimal(string source)
    {
        Mutate(source, ("price", decimal.MaxValue)).ShouldBe(decimal.MaxValue);
        Mutate(source, ("price", decimal.MinValue)).ShouldBe(decimal.MinValue);
    }

    [Fact]
    public void Greatest_and_least_of_two_ints_are_ints()
    {
        Mutate("math.greatest(qty, 3)", ("qty", 7L)).ShouldBe(7L);
        Mutate("math.least(qty, 3)", ("qty", 7L)).ShouldBe(3L);
    }

    [Fact]
    public void Greatest_and_least_of_two_decimals_are_decimals()
    {
        Mutate("math.greatest(price, 2.5)", ("price", 1.25m)).ShouldBe(2.5m);
        Mutate("math.least(price, 2.5)", ("price", 1.25m)).ShouldBe(1.25m);
    }

    [Fact]
    public void An_int_and_a_decimal_bind_the_decimal_overload() =>
        Mutate("math.greatest(qty, 2.5)", ("qty", 1L)).ShouldBe(2.5m);

    /// <summary>When the Int wins, the answer is still the Decimal overload's: a decimal 3, never a long.</summary>
    [Fact]
    public void A_winning_int_beside_a_decimal_comes_back_as_a_decimal()
    {
        Mutate("math.greatest(qty, 2.5)", ("qty", 3L)).ShouldBeOfType<decimal>().ShouldBe(3m);
        Mutate("math.least(qty, 3.5)", ("qty", 3L)).ShouldBeOfType<decimal>().ShouldBe(3m);
    }

    [Theory]
    [InlineData("math.ceil(price)", "2.0")]
    [InlineData("math.floor(price)", "1.80")]
    public void Ceil_and_floor_answer_a_whole_number_with_no_fraction_digits(string source, string value) =>
        Mutate(source, ("price", Number(value))).ShouldBeOfType<decimal>().Scale.ShouldBe((byte)0);

    [Theory]
    [InlineData("math.greatest(price, 1.00)")]
    [InlineData("math.least(price, 1.00)")]
    public void Equal_values_answer_the_first_argument(string source)
    {
        var answer = Mutate(source, ("price", 1.0m)).ShouldBeOfType<decimal>();

        answer.Scale.ShouldBe((byte)1, "the first argument's 1.0, not the second's 1.00");
    }

    [Theory]
    [InlineData("math.greatest(qty, 3)")]
    [InlineData("math.least(3, qty)")]
    [InlineData("math.ceil(price)")]
    [InlineData("math.floor(price)")]
    public void A_null_argument_makes_the_call_null(string source) =>
        Mutate(source, ("qty", null), ("price", null)).ShouldBeNull();

    [Theory]
    [InlineData("2.345", 2L, "2.35")]
    [InlineData("-2.345", 2L, "-2.35")]
    [InlineData("2.5", 0L, "3")]
    [InlineData("0.125", 2L, "0.13")]
    public void Round_to_digits_halves_away_from_zero(string value, long digits, string expected) =>
        Mutate($"math.round(price, {digits})", ("price", Number(value))).ShouldBe(Number(expected));

    [Fact]
    public void Round_to_more_digits_than_the_value_has_pads_nothing() =>
        Mutate("math.round(price, 5)", ("price", 1.2m)).ShouldBeOfType<decimal>().Scale.ShouldBe((byte)1);

    [Fact]
    public void Round_to_digits_widens_an_int() => Mutate("math.round(qty, 2)", ("qty", 7L)).ShouldBe(7m);

    [Theory]
    [InlineData(0L)]
    [InlineData(28L)]
    public void Round_to_digits_accepts_both_ends_of_0_to_28(long digits) =>
        CelBuiltInFunctions.RoundTo(1.5m, digits).ShouldBe(Math.Round(1.5m, (int)digits, MidpointRounding.AwayFromZero));

    [Theory]
    [InlineData(29L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(4294967324L)]
    public void Round_to_digits_outside_0_to_28_fails_closed_when_computed(long digits)
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("math.round(price, qty)", ("price", 1.5m), ("qty", digits)));

        failure.FunctionName.ShouldBe("math.round");
        failure.Reason.ShouldBe("digits must be from 0 to 28");
    }

    [Fact]
    public void Round_to_digits_fails_closed_in_a_condition_rather_than_answering_false() =>
        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(
                TestCelFunctions.Compile("math.round(new.price, new.qty) > 1.0", CelProfile.Condition),
                CelFixtures.Row(("price", 1.5m), ("qty", 29L)), previous: null, AlvoContext.Anonymous))
            .FunctionName.ShouldBe("math.round");

    [Fact]
    public void Round_to_digits_of_a_null_is_null() => Mutate("math.round(price, 2)", ("price", null)).ShouldBeNull();

    [Fact]
    public void Round_to_a_null_digits_is_null() => Mutate("math.round(price, qty)", ("price", 1.5m), ("qty", null)).ShouldBeNull();

    [Fact]
    public void Greatest_takes_exactly_two_arguments() =>
        TestCelFunctions.Compiler().Compile("math.greatest(qty, 1, 2)", CelProfile.Mutate, TestCelFunctions.Items).Errors[0].Message
            .ShouldBe("'math.greatest' takes 2 arguments; this call passes 3.");

    [Theory]
    [InlineData("math.ceil(price)", "Decimal")]
    [InlineData("math.floor(qty)", "Int")]
    [InlineData("math.greatest(qty, 1)", "Int")]
    [InlineData("math.least(price, 1)", "Decimal")]
    [InlineData("math.round(qty, 2)", "Decimal")]
    public void Each_math_built_in_has_its_result_type(string source, string expected) =>
        TestCelFunctions.Compile(source, CelProfile.Mutate).ResultType.ToString().ShouldBe(expected);

    [Theory]
    [InlineData("math.ceil(price) > 1", CelProfile.Rule)]
    [InlineData("math.greatest(price, 1)", CelProfile.Computed)]
    [InlineData("math.round(price, 2) > 1", CelProfile.Access)]
    public void A_math_built_in_is_refused_where_sql_renders(string source, CelProfile profile)
    {
        var refused = TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items);

        refused.IsSuccess.ShouldBeFalse();
        refused.Errors[0].Message.ShouldStartWith($"'{source[..source.IndexOf('(', StringComparison.Ordinal)]}(...)' is not available in the {profile} profile");
    }
}
