using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The two ASCII folds are ordinary built-ins: any String argument, Condition and Mutate (spec E4).</summary>
public sealed class CelCaseFoldTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData("ABC def", "abc def")]
    [InlineData("ÄBC", "Äbc")]
    [InlineData("ẞ", "ẞ")]
    [InlineData("", "")]
    public void Lower_ascii_folds_a_to_z_and_nothing_else(string text, string expected) =>
        Mutate("lowerAscii(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("abc DEF", "ABC DEF")]
    [InlineData("äbc", "äBC")]
    [InlineData("ß", "ß")]
    public void Upper_ascii_folds_a_to_z_and_nothing_else(string text, string expected) =>
        Mutate("upperAscii(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("lowerAscii(trim(name))", "  AB ", "ab")]
    [InlineData("upperAscii(replace(name, ' ', ''))", "ab cd", "ABCD")]
    [InlineData("lowerAscii('ABC')", null, "abc")]
    public void A_fold_takes_any_string_expression(string source, string? name, string expected) =>
        Mutate(source, ("name", name)).ShouldBe(expected);

    [Fact]
    public void A_null_argument_folds_to_null() => Mutate("lowerAscii(name)", ("name", null)).ShouldBeNull();

    [Theory]
    [InlineData("lowerAscii(new.name) == 'x'")]
    [InlineData("upperAscii(old.name) != 'X'")]
    public void A_fold_is_legal_in_a_condition(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("lowerAscii(name) == 'x'", CelProfile.Rule)]
    [InlineData("lowerAscii(name)", CelProfile.Computed)]
    public void A_fold_is_refused_where_sql_renders_with_the_function_gate(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors[0].Message
            .ShouldStartWith($"'lowerAscii(...)' is not available in the {profile} profile");

    [Fact]
    public void Lower_ascii_takes_one_argument() =>
        TestCelFunctions.Compiler().Compile("lowerAscii()", CelProfile.Mutate, TestCelFunctions.Items).Errors[0].Message
            .ShouldBe("'lowerAscii' takes 1 argument; this call passes 0.");

    [Fact]
    public void Now_is_still_the_one_legacy_call_and_mutate_only() =>
        TestCelFunctions.Compiler().Compile("now() == now()", CelProfile.Condition, TestCelFunctions.Items).Errors[0].Message
            .ShouldStartWith("'now(...)' is legal only in the Mutate profile");
}
