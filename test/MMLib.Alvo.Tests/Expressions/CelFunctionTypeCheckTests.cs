using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A catalogued call is resolved against its signatures and gated by its profiles, one error per problem.</summary>
public sealed class CelFunctionTypeCheckTests
{
    private static CelCompilationResult Compile(string source, CelProfile profile, params CelFunction[] functions) =>
        TestCelFunctions.Compiler(functions).Compile(source, profile, TestCelFunctions.Items);

    private static CelFunction Magnitude(CelValueType type) => new(
        "magnitude", [TestCelFunctions.Parameter("x", type)], type, ResultNullable: false, "A test overload.",
        IsHost: false, CelBuiltInFunctions.ConditionAndMutate, arguments => arguments[0]);

    private static CelFunction WithProfiles(params CelProfile[] profiles) => new(
        "probe", [TestCelFunctions.Parameter("s", CelValueType.String)], CelValueType.String, ResultNullable: false, "A test function.",
        IsHost: true, new HashSet<CelProfile>(profiles), arguments => arguments[0]);

    private static readonly CelProfile[] _everyProfile =
        [CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate, CelProfile.Access];

    [Fact]
    public void A_call_in_a_hook_condition_binds_its_overload_and_result_type()
    {
        var compiled = Compile("echo(name) == 'x'", CelProfile.Condition, TestCelFunctions.Echo);

        compiled.IsSuccess.ShouldBeTrue();
        var call = ((CelBinary)compiled.Expression!.Root).Left.ShouldBeOfType<CelCall>();
        call.Function.ShouldNotBeNull().Name.ShouldBe("echo");
        call.ResultType.ShouldBe(CelValueType.String);
    }

    [Fact]
    public void A_call_is_a_mutate_value_of_its_result_type()
    {
        var compiled = Compile("echo(new.name)", CelProfile.Mutate, TestCelFunctions.Echo);

        compiled.IsSuccess.ShouldBeTrue();
        compiled.Expression!.ResultType.ShouldBe(CelValueType.String);
    }

    [Theory]
    [InlineData(CelProfile.Rule, "echo(name) == 'x'", "Rule", "before-hook mutate")]
    [InlineData(CelProfile.Computed, "echo(name)", "Computed", "regular field")]
    [InlineData(CelProfile.Access, "echo('a') == 'a'", "Access", "@user.roles")]
    public void A_function_outside_its_profiles_is_refused_once_with_why_and_where(CelProfile profile, string source, string named, string fix)
    {
        var refused = Compile(source, profile, TestCelFunctions.Echo);

        var error = refused.Errors.ShouldHaveSingleItem();
        error.Message.ShouldStartWith($"'echo(...)' is not available in the {named} profile; it is available in Condition and Mutate.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain(fix);
    }

    [Theory]
    [InlineData(CelProfile.Rule, "probe(name) == 'x'")]
    [InlineData(CelProfile.Computed, "probe(name)")]
    [InlineData(CelProfile.Access, "probe('a') == 'a'")]
    public void The_ceiling_refuses_a_function_whose_own_profiles_include_the_profile(CelProfile profile, string source)
    {
        var error = Compile(source, profile, WithProfiles(_everyProfile)).Errors.ShouldHaveSingleItem();

        error.Message.ShouldStartWith($"'probe(...)' is not available in the {profile} profile; it is available in Condition and Mutate.");
    }

    [Fact]
    public void A_mutate_only_function_is_refused_in_condition_with_a_neutral_reason()
    {
        var error = Compile("probe(name) == 'x'", CelProfile.Condition, WithProfiles(CelProfile.Mutate)).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "'probe(...)' is not available in the Condition profile; it is available in Mutate. "
            + "This function is not enabled for the Condition profile.");
        error.FixSuggestion.ShouldNotBeNull().ShouldNotContain("@user.roles");
    }

    [Fact]
    public void A_function_with_no_profiles_says_it_is_available_nowhere()
    {
        var error = Compile("probe(name)", CelProfile.Mutate, WithProfiles()).Errors.ShouldHaveSingleItem();

        error.Message.ShouldStartWith("'probe(...)' is not available in the Mutate profile; it is available in no profile.");
    }

    [Fact]
    public void The_available_list_holds_only_profiles_inside_the_ceiling_and_never_the_refused_one()
    {
        var message = Compile("probe(name)", CelProfile.Computed, WithProfiles(_everyProfile)).Errors.ShouldHaveSingleItem().Message;

        message.ShouldContain("it is available in Condition and Mutate.");
        message.ShouldNotContain("Access");
    }

    [Fact]
    public void A_wrong_arity_names_the_signature()
    {
        var error = Compile("echo(name, name)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'echo' takes 1 argument; this call passes 2.");
        error.FixSuggestion.ShouldBe("Call it as echo(s: String) -> String?.");
    }

    [Fact]
    public void A_wrong_argument_type_names_what_was_passed_and_what_is_accepted() =>
        Compile("echo(price)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'echo(...)' accepts no (Decimal); it accepts echo(s: String) -> String?.");

    [Fact]
    public void An_int_binds_a_decimal_parameter()
    {
        var compiled = Compile("half(qty)", CelProfile.Mutate, TestCelFunctions.Half);

        compiled.IsSuccess.ShouldBeTrue();
        compiled.Expression!.ResultType.ShouldBe(CelValueType.Decimal);
    }

    [Fact]
    public void An_exact_overload_wins_over_a_widened_one()
    {
        var overloads = new[] { Magnitude(CelValueType.Int), Magnitude(CelValueType.Decimal) };

        Compile("magnitude(qty)", CelProfile.Mutate, overloads).Expression!.ResultType.ShouldBe(CelValueType.Int);
        Compile("magnitude(price)", CelProfile.Mutate, overloads).Expression!.ResultType.ShouldBe(CelValueType.Decimal);
    }

    [Fact]
    public void A_null_literal_fits_only_a_parameter_that_receives_null()
    {
        Compile("echo(null)", CelProfile.Mutate, TestCelFunctions.Echo).IsSuccess.ShouldBeFalse();
        Compile("isBlank(null)", CelProfile.Condition, TestCelFunctions.IsBlank).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_bad_argument_is_reported_once_and_does_not_cascade() =>
        Compile("echo(nope)", CelProfile.Mutate, TestCelFunctions.Echo).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'nope' is not a field of entity 'items'.");

    [Fact]
    public void A_wrong_profile_and_a_wrong_arity_are_two_independent_errors() =>
        Compile("echo(name, name) == 'x'", CelProfile.Rule, TestCelFunctions.Echo).Errors.Count.ShouldBe(2);

    [Fact]
    public void A_field_named_like_a_built_in_is_a_field_argument() =>
        Compile("echo(size) == round", CelProfile.Condition, TestCelFunctions.Echo).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_function_named_like_a_field_reads_the_field_as_its_argument()
    {
        var title = TestCelFunctions.Host("name", CelValueType.String, arguments => arguments[0], TestCelFunctions.Parameter("s", CelValueType.String));

        Compile("name(name) == name", CelProfile.Condition, title).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_legacy_calls_keep_their_mutate_only_refusal_text() =>
        Compile("now() == now()", CelProfile.Condition).Errors[0].Message
            .ShouldBe("'now(...)' is legal only in the Mutate profile (a before-hook mutate value).");
}
