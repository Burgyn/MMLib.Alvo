using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A bound call: values marshalled, nulls propagated, failures never collapsed into a value.</summary>
public sealed class CelFunctionInvocationTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, AlvoRecord row, params CelFunction[] functions) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate, functions), row, previous: null, _now);

    private static bool Condition(string source, AlvoRecord row, params CelFunction[] functions) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition, functions), row, previous: null, CelFixtures.Alice);

    private static CelFunction Counting(Action counted) => TestCelFunctions.Host(
        "count", CelValueType.String, arguments => { counted(); return arguments[0]; }, TestCelFunctions.Parameter("s", CelValueType.String));

    private static CelFunction Echoing(string name, CelValueType type) =>
        TestCelFunctions.Host(name, type, arguments => arguments[0], TestCelFunctions.Parameter("v", type));

    private static CelFunction Boom => TestCelFunctions.Host(
        "boom", CelValueType.String, _ => throw new FormatException("host secret"), TestCelFunctions.Parameter("s", CelValueType.String));

    [Fact]
    public void A_bound_call_invokes_the_function_with_the_argument_value() =>
        Mutate("echo(name)", CelFixtures.Row(("name", "Ada")), TestCelFunctions.Echo).ShouldBe("Ada");

    [Fact]
    public void A_null_argument_for_a_parameter_that_takes_none_is_null_without_invoking()
    {
        var calls = 0;

        Mutate("count(name)", CelFixtures.Row(("name", null)), Counting(() => calls++)).ShouldBeNull();
        calls.ShouldBe(0);
    }

    [Fact]
    public void A_nullable_parameter_receives_null() =>
        Condition("isBlank(name)", CelFixtures.Row(("name", null)), TestCelFunctions.IsBlank).ShouldBeTrue();

    [Fact]
    public void A_value_of_an_unexpected_clr_type_fails_the_call_without_invoking()
    {
        var calls = 0;

        Should.Throw<CelFunctionException>(() => Mutate("count(name)", CelFixtures.Row(("name", 5)), Counting(() => calls++)))
            .Reason.ShouldBe("an argument does not fit parameter 's' (String)");
        calls.ShouldBe(0);
    }

    [Fact]
    public void An_int_runtime_value_binds_a_decimal_parameter() =>
        Mutate("half(qty)", CelFixtures.Row(("qty", 3)), TestCelFunctions.Half).ShouldBe(1.5m);

    [Fact]
    public void A_date_column_value_binds_a_timestamp_parameter_as_midnight_utc() =>
        Mutate("stamp(due)", CelFixtures.Row(("due", new DateOnly(2031, 3, 4))), Echoing("stamp", CelValueType.Timestamp))
            .ShouldBe(new DateTimeOffset(2031, 3, 4, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_guid_text_binds_a_uuid_parameter() =>
        Mutate("id(ref_id)", CelFixtures.Row(("ref_id", "6f9619ff-8b86-d011-b42d-00c04fc964ff")), Echoing("id", CelValueType.Uuid))
            .ShouldBe(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));

    [Fact]
    public void An_int_result_is_returned_as_a_cel_int()
    {
        var width = TestCelFunctions.Host("width", CelValueType.Int, _ => 5, TestCelFunctions.Parameter("s", CelValueType.String));

        Mutate("width(name)", CelFixtures.Row(("name", "x")), width).ShouldBeOfType<long>().ShouldBe(5L);
    }

    [Fact]
    public void A_throwing_function_in_a_mutate_escapes_as_a_function_failure()
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("boom(name)", CelFixtures.Row(("name", "x")), Boom));

        failure.FunctionName.ShouldBe("boom");
        failure.IsHost.ShouldBeTrue();
        failure.InnerException.ShouldBeOfType<FormatException>();
        failure.Message.ShouldNotContain("host secret");
    }

    [Fact]
    public void A_throwing_function_in_a_condition_escapes_instead_of_reading_as_false() =>
        Should.Throw<CelFunctionException>(() => Condition("boom(name) == 'x'", CelFixtures.Row(("name", "x")), Boom));

    [Fact]
    public void A_failure_that_carries_a_reason_passes_through_unchanged()
    {
        var capped = TestCelFunctions.Host(
            "capped", CelValueType.String, _ => throw new CelFunctionException("capped", "too long"), TestCelFunctions.Parameter("s", CelValueType.String));

        Should.Throw<CelFunctionException>(() => Mutate("capped(name)", CelFixtures.Row(("name", "x")), capped)).Reason.ShouldBe("too long");
    }

    [Fact]
    public void A_function_is_invoked_exactly_once_per_evaluation()
    {
        var calls = 0;

        Condition("count(name) == 'x'", CelFixtures.Row(("name", "x")), Counting(() => calls++)).ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void A_function_that_answers_null_makes_a_comparison_false()
    {
        var nothing = TestCelFunctions.Host("nothing", CelValueType.String, _ => null, TestCelFunctions.Parameter("s", CelValueType.String));

        Condition("nothing(name) == 'x'", CelFixtures.Row(("name", "x")), nothing).ShouldBeFalse();
        Condition("!(nothing(name) == 'x')", CelFixtures.Row(("name", "x")), nothing).ShouldBeTrue();
    }

    [Fact]
    public void The_two_legacy_calls_still_evaluate_by_name()
    {
        Mutate("lowerAscii(name)", CelFixtures.Row(("name", "ABC"))).ShouldBe("abc");
        Mutate("now()", CelFixtures.Row()).ShouldBe(_now);
    }
}
