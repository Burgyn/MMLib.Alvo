using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// String '+' in a mutate value (spec §5.6, F18, D-6): it joins two strings, a null operand makes the value null, and a
/// number is joined only through string(). A computed field keeps refusing a nullable operand.
/// </summary>
public sealed class CelMutateConcatenationTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items);

    [Theory]
    [InlineData("'+421' + name", "905100200", "+421905100200")]
    [InlineData("name + '-' + name", "ab", "ab-ab")]
    [InlineData("'FR-' + upperAscii(trim(name))", "  wtu1 ", "FR-WTU1")]
    [InlineData("'#' + string(qty)", null, null)]
    public void A_mutate_joins_strings(string source, string? name, string? expected) =>
        Mutate(source, ("name", name), ("qty", null)).ShouldBe(expected);

    [Fact]
    public void A_number_joins_through_string() => Mutate("'#' + string(qty)", ("qty", 7L)).ShouldBe("#7");

    [Fact]
    public void A_null_operand_makes_the_value_null() => Mutate("'+421' + name", ("name", null)).ShouldBeNull();

    /// <summary>
    /// A present value that is no string in a string field — only an embedded caller's own record can hold one — is not
    /// joined and not nulled: '+' falls to the arithmetic, which fails closed on it (Ruling P).
    /// </summary>
    [Fact]
    public void A_present_value_that_is_no_string_fails_the_join_closed()
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("name + 'x'", ("name", 7L)));

        failure.FunctionName.ShouldBe("_+_");
        failure.Reason.ShouldBe("an operand is not an Int or a Decimal");
    }

    [Fact]
    public void A_nullable_field_is_admitted_in_a_mutate() => Compile("name + 'x'", CelProfile.Mutate).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_number_without_string_is_refused_with_string_as_the_fix()
    {
        var error = Compile("'#' + qty", CelProfile.Mutate).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'+' joins two strings or adds two numbers; found String and Int, and CEL converts neither implicitly.");
        error.FixSuggestion.ShouldBe("Write string(x) to join a number, a flag, an id or an instant.");
    }

    /// <summary>
    /// A condition cannot join at all, so its mixed-pair fix keeps the computed-field advice: <c>string(x)</c> would only
    /// lead the author to the gate refusal next (preflight R-10).
    /// </summary>
    [Fact]
    public void A_condition_is_not_told_to_reach_for_string() =>
        Compile("'#' + new.qty == '#7'", CelProfile.Condition).Errors
            .ShouldNotContain(error => error.FixSuggestion != null && error.FixSuggestion.Contains("string(x)", StringComparison.Ordinal));

    [Fact]
    public void Concatenation_stays_refused_in_a_condition() =>
        Compile("name + 'x' == 'ax'", CelProfile.Condition).Errors
            .ShouldContain(error => error.Message == "String concatenation ('+' over two strings) is legal only in the Computed and Mutate profiles.");

    /// <summary>
    /// A join is capped at the text length <c>replace</c> may grow to (preflight S-2, R-2): about 600 joins of a 1 MiB
    /// field fit in a 2,000-character source, and the refusal comes before the allocation, so it is a fail-closed
    /// <c>_+_</c> rather than an out-of-memory the mutate's catch would turn into a silent null write.
    /// </summary>
    [Fact]
    public void A_join_past_the_text_cap_fails_closed()
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("name + name", ("name", new string('a', 600_000))));

        failure.FunctionName.ShouldBe("_+_");
        failure.Reason.ShouldBe("its result would be 1,200,000 characters, over the 1,048,576 a text may grow to here");
    }

    [Fact]
    public void A_join_exactly_at_the_text_cap_is_written() =>
        Mutate("name + name", ("name", new string('a', CelBuiltInFunctions.MaxTextLength / 2)))
            .ShouldBeOfType<string>().Length.ShouldBe(CelBuiltInFunctions.MaxTextLength);

    /// <summary>
    /// The cap is the hook path's: a computed join keeps the semantics of the SQL <c>||</c> it renders to, whose operands
    /// are bounded by their own columns. A cap there would answer <see langword="null"/>, which this value fact sees.
    /// </summary>
    [Fact]
    public void A_computed_join_is_not_capped()
    {
        var expression = CelFixtures.Compiler.Compile("first_name + last_name", CelProfile.Computed, CelStringConcatenationTests.Customers)
            .Expression.ShouldNotBeNull();
        var half = new string('a', 600_000);

        CelInterpreter.EvaluateScalar(expression, CelFixtures.Row(("first_name", half), ("last_name", half)))
            .ShouldBeOfType<string>().Length.ShouldBe(1_200_000);
    }
}
