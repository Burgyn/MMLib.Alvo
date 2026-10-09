using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A built-in call over literals only is evaluated once at apply: if it can only fail, the descriptor is refused there
/// rather than answering function-failed on every write (spec E5, deviation F15). Host code never runs at apply.
/// </summary>
public sealed class CelConstantCallTests
{
    [Theory]
    [InlineData("new.due < timestamp('2026-13-01T00:00:00Z')", "timestamp", "the text is not an RFC 3339 timestamp")]
    [InlineData("substring('abc', 0, 5) == 'x'", "substring", "a position is outside the text")]
    [InlineData("int('seven') == 7", "int", "the text is not a whole number")]
    public void A_constant_call_that_always_fails_is_refused_with_its_reason(string source, string function, string reason)
    {
        var error = TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldStartWith($"'{function}(...)' always fails with these constant arguments: {reason}");
        error.FixSuggestion.ShouldBe("Correct the constant, or pass a field instead of a literal.");
        error.Position.ShouldBe(source.IndexOf(function, StringComparison.Ordinal));
    }

    [Fact]
    public void The_refusal_quotes_the_whole_reason_and_ends_with_one_period() =>
        TestCelFunctions.Compiler().Compile("int('seven')", CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'int(...)' always fails with these constant arguments: the text is not a whole number such as 42 or -7.");

    [Theory]
    [InlineData("new.due < timestamp('2026-10-05T12:00:00Z')")]
    [InlineData("substring(new.name, 0, 5) == 'x'")]
    [InlineData("math.ceil(1.5) == 2")]
    [InlineData("startsWith('abc', 'a')")]
    public void A_constant_call_that_succeeds_or_reads_a_field_compiles(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    /// <summary>
    /// The check is shallow by construction (preflight F6-1): only a call whose own arguments are literals is evaluated,
    /// so <c>int(trim('seven'))</c> compiles and fails each write at run time instead, as spec §6.4 words it.
    /// </summary>
    [Fact]
    public void A_nested_constant_call_is_not_evaluated() =>
        TestCelFunctions.Compiler().Compile("int(trim('seven')) == 7", CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_host_function_with_literal_arguments_is_never_run_at_apply()
    {
        var calls = 0;
        var counting = TestCelFunctions.Host("count", CelValueType.Bool, _ => ++calls > 0, TestCelFunctions.Parameter("s", CelValueType.String));

        TestCelFunctions.Compiler(counting).Compile("count('x')", CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

        calls.ShouldBe(0);
    }

    [Fact]
    public void A_failing_host_function_with_literal_arguments_compiles() =>
        TestCelFunctions.Compiler(TestCelFunctions.Host("boom", CelValueType.Bool, _ => throw new InvalidOperationException("boom"), TestCelFunctions.Parameter("s", CelValueType.String)))
            .Compile("boom('x')", CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    /// <summary>
    /// A declared constant check runs although another argument is a field (spec §6.4, E17): with <c>digits</c> 30 the
    /// call can never produce a value, whatever the price — a present one fails it, a null one makes it null. <c>-1</c>
    /// is a negation, not a literal, so it fails at run time instead (Task 4).
    /// </summary>
    [Theory]
    [InlineData("math.round(price, 29)")]
    [InlineData("math.round(new.price, 30)")]
    [InlineData("math.round(2.5, 100)")]
    public void A_literal_digits_out_of_range_is_refused_although_x_may_be_a_field(string source)
    {
        var error = TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'math.round(...)' always fails with these constant arguments: digits must be from 0 to 28.");
        error.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData("math.round(price, 0)")]
    [InlineData("math.round(price, 2)")]
    [InlineData("math.round(price, 28)")]
    [InlineData("math.round(price, qty)")]
    public void Digits_in_range_or_computed_compile(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_call_refused_by_its_profile_is_not_evaluated_too() =>
        TestCelFunctions.Compiler().Compile("int('seven') == 7", CelProfile.Rule, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldStartWith("'int(...)' is not available in the Rule profile");

    /// <summary>
    /// A computed field answers <see langword="null"/> for a division by zero, it never fails (spec §5.6), so a literal
    /// zero divisor there is not an "always fails" call and stays legal: Ruling N's refusal is for the hook profiles,
    /// where Task 7 makes the division fail closed.
    /// </summary>
    [Theory]
    [InlineData("price / 0")]
    [InlineData("qty / 0")]
    [InlineData("qty / 0.0")]
    [InlineData("qty / 0.00")]
    public void A_literal_zero_divisor_in_a_computed_field_still_compiles(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Computed, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    /// <summary>
    /// Ruling N (preflight S-3): in the two profiles whose division fails closed (spec §5.6), a literal zero divisor can
    /// never produce a value, so it is refused at apply — an Int zero, a decimal zero, and a decimal zero with a scale.
    /// </summary>
    [Theory]
    [InlineData("new.qty / 0 > 1", CelProfile.Condition)]
    [InlineData("new.price / 0.0 > 1", CelProfile.Condition)]
    [InlineData("new.qty / 0.00 > 1", CelProfile.Condition)]
    [InlineData("qty / 0", CelProfile.Mutate)]
    [InlineData("price / 0.0", CelProfile.Mutate)]
    [InlineData("qty / 0.00", CelProfile.Mutate)]
    public void A_literal_zero_divisor_in_a_hook_is_refused_at_apply(string source, CelProfile profile)
    {
        var error = TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'/' always fails with this constant divisor: the divisor is zero.");
        error.FixSuggestion.ShouldBe("Correct the constant, or pass a field instead of a literal.");
        error.Position.ShouldBe(source.IndexOf(" /", StringComparison.Ordinal), "the division's own position, where every arithmetic error sits");
    }

    /// <summary>The refusal is shallow (spec §5.6): a divisor that is not itself a literal fails each write at run time.</summary>
    [Theory]
    [InlineData("qty / (1 - 1)")]
    [InlineData("qty / -0")]
    [InlineData("qty / 2")]
    public void A_divisor_that_is_not_a_literal_zero_compiles(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    /// <summary>
    /// A built-in whose body throws something other than its own refusal — a regex timeout, say — has no reason Alvo can
    /// quote, and its failure may not repeat: the apply does not refuse, and run time decides (preflight carry-over).
    /// </summary>
    [Fact]
    public void A_constant_call_whose_body_throws_without_a_reason_compiles()
    {
        var throwing = TestCelFunctions.Host("flaky", CelValueType.Bool, _ => throw new TimeoutException("regex"), TestCelFunctions.Parameter("s", CelValueType.String));
        var flaky = throwing with { IsHost = false };

        TestCelFunctions.Compiler(flaky).Compile("flaky('x')", CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();
    }
}
