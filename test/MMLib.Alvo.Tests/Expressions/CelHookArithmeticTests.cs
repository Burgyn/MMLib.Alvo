using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Arithmetic in a hook condition and a mutate value (spec §5.6, D-7): CEL's semantics — Int stays Int, '/' truncates
/// toward zero, an overflow or a division by zero is an error — and the error fails closed like a function's. A
/// computed field keeps its decimal arithmetic and keeps answering null for the same inputs.
/// </summary>
public sealed class CelHookArithmeticTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Theory]
    [InlineData("qty * 2", 7L, 14L)]
    [InlineData("qty / 2", 7L, 3L)]
    [InlineData("qty / 2", -7L, -3L)]
    [InlineData("qty - 10", 7L, -3L)]
    [InlineData("-qty", 7L, -7L)]
    public void Int_arithmetic_stays_int_and_division_truncates_toward_zero(string source, long qty, long expected) =>
        Mutate(source, ("qty", qty)).ShouldBe(expected);

    [Fact]
    public void An_int_and_a_decimal_make_a_decimal() => Mutate("qty + 0.5", ("qty", 1L)).ShouldBe(1.5m);

    [Fact]
    public void Money_to_cents_needs_no_more_than_this() =>
        Mutate("math.round(price * 1.2, 2)", ("price", 10.25m)).ShouldBe(12.30m);

    [Fact]
    public void A_null_operand_makes_the_result_null() => Mutate("qty * 2", ("qty", null)).ShouldBeNull();

    [Fact]
    public void A_null_operand_under_negation_makes_the_result_null() => Mutate("-price", ("price", null)).ShouldBeNull();

    [Fact]
    public void A_decimal_negates_to_a_decimal() => Mutate("-price", ("price", 2.5m)).ShouldBe(-2.5m);

    /// <summary>
    /// Every Int failure of spec §5.5. The zero divisor is a field: a literal one is refused at apply (Ruling N), so it
    /// could never reach the interpreter.
    /// </summary>
    [Theory]
    [InlineData("qty + 9223372036854775807", 1L, "_+_", "the result is outside the range of an Int")]
    [InlineData("qty - 9223372036854775807", -2L, "_-_", "the result is outside the range of an Int")]
    [InlineData("qty * 9223372036854775807", 2L, "_*_", "the result is outside the range of an Int")]
    [InlineData("qty / qty", 0L, "_/_", "the divisor is zero")]
    [InlineData("qty / -1", long.MinValue, "_/_", "the result is outside the range of an Int")]
    [InlineData("-qty", long.MinValue, "-_", "the result is outside the range of an Int")]
    public void An_int_failure_in_a_mutate_fails_closed(string source, long qty, string name, string reason) =>
        ShouldFailClosed(() => Mutate(source, ("qty", qty)), name, reason);

    [Theory]
    [InlineData("price * price", "79228162514264337593543950335", "_*_", "the result is outside the range of a Decimal")]
    [InlineData("price + price", "79228162514264337593543950335", "_+_", "the result is outside the range of a Decimal")]
    [InlineData("-price - price", "79228162514264337593543950335", "_-_", "the result is outside the range of a Decimal")]
    [InlineData("price / price", "0", "_/_", "the divisor is zero")]
    [InlineData("price / price", "0.00", "_/_", "the divisor is zero")]
    public void A_decimal_failure_in_a_mutate_fails_closed(string source, string price, string name, string reason) =>
        ShouldFailClosed(() => Mutate(source, ("price", decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture))), name, reason);

    [Fact]
    public void A_decimal_quotient_past_the_range_fails_closed() =>
        ShouldFailClosed(() => Mutate("price / 0.5", ("price", decimal.MaxValue)), "_/_", "the result is outside the range of a Decimal");

    /// <summary>An Int divided by a decimal zero takes the decimal path, and its zero is still a zero.</summary>
    [Fact]
    public void An_int_divided_by_a_decimal_zero_fails_closed() =>
        ShouldFailClosed(() => Mutate("qty / price", ("qty", 7L), ("price", 0m)), "_/_", "the divisor is zero");

    /// <summary>The fail-open this task closes: a reject whose condition divides by zero refuses, never skips.</summary>
    [Fact]
    public void A_division_by_zero_in_a_condition_fails_closed_rather_than_reading_false() =>
        ShouldFailClosed(() => Condition("new.price / new.qty > 1", ("price", 5m), ("qty", 0L)), "_/_", "the divisor is zero");

    /// <summary>A negated comparison cannot turn the failure into <see langword="true"/> either: it escapes before <c>!</c>.</summary>
    [Fact]
    public void A_negated_condition_over_a_failure_still_fails_closed() =>
        ShouldFailClosed(() => Condition("!(new.qty * 9223372036854775807 > 0)", ("qty", 2L)), "_*_", "the result is outside the range of an Int");

    [Theory]
    [InlineData("new.qty + 9223372036854775807 > 0", "_+_")]
    [InlineData("-new.qty > 0", "-_")]
    public void An_int_overflow_in_a_condition_fails_closed(string source, string name)
    {
        var qty = name == "-_" ? long.MinValue : 1L;

        ShouldFailClosed(() => Condition(source, ("qty", qty)), name, "the result is outside the range of an Int");
    }

    /// <summary>The product's own evaluator, the one a before-hook gate calls, lets the failure escape too.</summary>
    [Fact]
    public void The_product_evaluator_lets_the_failure_escape() =>
        Should.Throw<CelFunctionException>(() => CelFixtures.Evaluator.Evaluate(
            TestCelFunctions.Compile("new.price / new.qty > 1", CelProfile.Condition), CelFixtures.Row(("price", 5m), ("qty", 0L)), null, AlvoContext.Anonymous))
            .FunctionName.ShouldBe("_/_");

    [Fact]
    public void A_condition_compares_arithmetic() => Condition("new.qty * 2 > 10", ("qty", 6L)).ShouldBeTrue();

    [Fact]
    public void A_condition_compares_truncated_division() => Condition("new.qty / 2 == 3", ("qty", 7L)).ShouldBeTrue();

    [Fact]
    public void A_null_operand_in_a_condition_does_not_fire() => Condition("new.qty * 2 > 10", ("qty", null)).ShouldBeFalse();

    /// <summary>
    /// Gated on the profile (spec §5.6, D-7, preflight R-4): Computed keeps its decimal path, so the values differ from a
    /// hook's — <c>7 / 2</c> is 3.5 there, and an Int sum past the Int range is a decimal there, not an overflow. These
    /// facts fail if a computed field ever took the hook path, which a "does not throw" fact could not see.
    /// </summary>
    [Theory]
    [InlineData("qty / 2", 7L, "3.5")]
    [InlineData("qty + 9223372036854775807", 1L, "9223372036854775808")]
    [InlineData("-qty", long.MinValue, "9223372036854775808")]
    [InlineData("qty * 2", 7L, "14")]
    public void A_computed_field_keeps_its_decimal_arithmetic(string source, long qty, string expected) =>
        Computed(source, ("qty", qty)).ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A computed division by zero is still <see langword="null"/>. This fact alone cannot fail on a mis-gated flag —
    /// <see cref="CelInterpreter.EvaluateScalar"/> swallows every exception into <see langword="null"/> — so the value facts
    /// above are what pin the gate; this one pins the answer.
    /// </summary>
    [Fact]
    public void A_computed_division_by_zero_is_still_null() => Computed("price / qty", ("price", 5m), ("qty", 0L)).ShouldBeNull();

    [Theory]
    [InlineData("new.qty + 1 > 1", CelProfile.Condition)]
    [InlineData("-new.qty < 0", CelProfile.Condition)]
    [InlineData("-new.qty", CelProfile.Mutate)]
    [InlineData("new.price * new.qty", CelProfile.Mutate)]
    [InlineData("qty / 2", CelProfile.Mutate)]
    public void Arithmetic_compiles_in_both_hook_profiles(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("qty * 2", "Int")]
    [InlineData("qty / 2", "Int")]
    [InlineData("qty / 2.0", "Decimal")]
    [InlineData("-qty", "Int")]
    public void A_mutate_value_keeps_cel_result_types(string source, string type) =>
        TestCelFunctions.Compile(source, CelProfile.Mutate).ResultType.ToString().ShouldBe(type);

    [Theory]
    [InlineData("qty + 1 > 1", CelProfile.Rule, "Arithmetic is legal only in the Computed, Condition and Mutate profiles; '+' is not allowed here.")]
    [InlineData("-qty > 1", CelProfile.Rule, "Arithmetic negation ('-') is legal only in the Computed, Condition and Mutate profiles.")]
    [InlineData("1 * 2 > 1", CelProfile.Access, "Arithmetic is legal only in the Computed, Condition and Mutate profiles; '*' is not allowed here.")]
    public void Arithmetic_elsewhere_names_the_profiles_that_admit_it(string source, CelProfile profile, string message) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors.ShouldContain(error => error.Message == message);

    /// <summary>The profiles whose arithmetic fails closed are exactly the two hook slots, read in one place.</summary>
    [Theory]
    [InlineData(CelProfile.Condition, true)]
    [InlineData(CelProfile.Mutate, true)]
    [InlineData(CelProfile.Rule, false)]
    [InlineData(CelProfile.Computed, false)]
    [InlineData(CelProfile.Access, false)]
    public void Only_the_hook_profiles_fail_closed(CelProfile profile, bool expected) =>
        CelHookArithmetic.FailsClosed(profile).ShouldBe(expected);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, AlvoContext.Anonymous);

    private static object? Computed(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateScalar(TestCelFunctions.Compile(source, CelProfile.Computed), CelFixtures.Row(row));

    private static void ShouldFailClosed(Action evaluate, string name, string reason)
    {
        var failure = Should.Throw<CelFunctionException>(evaluate);

        failure.FunctionName.ShouldBe(name);
        failure.Reason.ShouldBe(reason);
    }
}
