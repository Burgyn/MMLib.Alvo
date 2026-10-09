using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A present argument that does not convert to its parameter's CLR type fails the call closed — it never reads as
/// null, because a null verdict is a <c>false</c> condition and a <c>reject</c> that does not fire.
/// </summary>
public sealed class CelArgumentConversionTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>A host function over one parameter of <paramref name="clrType"/>, counting its calls.</summary>
    private static CelFunction Probe(CelValueType type, System.Type clrType, Action counted, bool nullable = false) => new(
        "probe", [new CelFunctionArgument("n", type, nullable, clrType)], CelValueType.Bool, ResultNullable: true,
        "Test probe.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, _ => { counted(); return true; });

    private static CelFunctionException Refused(CelValueType type, System.Type clrType, object value)
    {
        var calls = 0;
        var failure = Should.Throw<CelFunctionException>(() => Probe(type, clrType, () => calls++).Invoke([value]));
        calls.ShouldBe(0);
        return failure;
    }

    [Fact]
    public void An_int_outside_the_int32_range_fails_an_int32_parameter_and_names_the_parameter_not_the_value()
    {
        var failure = Refused(CelValueType.Int, typeof(int), 3_000_000_000L);

        failure.FunctionName.ShouldBe("probe");
        failure.IsHost.ShouldBeTrue();
        failure.Reason.ShouldBe("an argument does not fit parameter 'n' (Int32)");
        failure.Message.ShouldNotContain("3000000000");
    }

    [Fact]
    public void The_int32_boundaries_still_bind()
    {
        var calls = 0;

        Probe(CelValueType.Int, typeof(int), () => calls++).Invoke([(long)int.MaxValue]).ShouldBe(true);
        Probe(CelValueType.Int, typeof(int), () => calls++).Invoke([(long)int.MinValue]).ShouldBe(true);
        calls.ShouldBe(2);
        Refused(CelValueType.Int, typeof(int), (long)int.MaxValue + 1);
        Refused(CelValueType.Int, typeof(int), (long)int.MinValue - 1);
    }

    [Fact]
    public void A_nullable_int32_parameter_fails_on_overflow_instead_of_receiving_null()
    {
        var received = new List<object?>();
        var nullable = new CelFunction(
            "probe", [new CelFunctionArgument("n", CelValueType.Int, Nullable: true, typeof(int))], CelValueType.Bool, ResultNullable: true,
            "Test probe.", IsHost: true, CelBuiltInFunctions.ConditionAndMutate, arguments => { received.Add(arguments[0]); return true; });

        Should.Throw<CelFunctionException>(() => nullable.Invoke([3_000_000_000L]));
        nullable.Invoke([null]).ShouldBe(true);
        received.ShouldBe([null]);
    }

    [Fact]
    public void A_decimal_with_a_fraction_fails_an_int_parameter() =>
        Refused(CelValueType.Int, typeof(long), 2.5m).Reason.ShouldBe("an argument does not fit parameter 'n' (Int64)");

    [Fact]
    public void A_decimal_past_the_int64_range_fails_an_int_parameter() =>
        Refused(CelValueType.Int, typeof(long), 1e20m);

    [Fact]
    public void A_double_that_is_no_number_fails_a_decimal_parameter() =>
        Refused(CelValueType.Decimal, typeof(decimal), double.NaN).Reason.ShouldBe("an argument does not fit parameter 'n' (Decimal)");

    [Fact]
    public void A_text_that_is_no_guid_fails_a_uuid_parameter() =>
        Refused(CelValueType.Uuid, typeof(Guid), "not-a-guid").Reason.ShouldBe("an argument does not fit parameter 'n' (Guid)");

    [Fact]
    public void A_text_fails_a_bool_parameter() =>
        Refused(CelValueType.Bool, typeof(bool), "true").Reason.ShouldBe("an argument does not fit parameter 'n' (Boolean)");

    [Fact]
    public void A_text_that_is_no_instant_fails_a_timestamp_parameter() =>
        Refused(CelValueType.Timestamp, typeof(DateTimeOffset), "yesterday").Reason.ShouldBe("an argument does not fit parameter 'n' (DateTimeOffset)");

    [Fact]
    public void A_stored_decimal_with_a_fraction_fails_a_built_in_over_an_int_field()
    {
        var compiled = TestCelFunctions.Compile("abs(qty)", CelProfile.Mutate);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluateMutation(compiled, CelFixtures.Row(("qty", 2.5m)), previous: null, _now))
            .FunctionName.ShouldBe("abs");
    }

    [Fact]
    public void An_unconvertible_argument_in_a_condition_escapes_instead_of_reading_as_false()
    {
        var gate = TestCelFunctions.Host("gate", CelValueType.Bool, _ => true, new CelFunctionArgument("n", CelValueType.Int, false, typeof(int)));
        var compiled = TestCelFunctions.Compile("gate(qty)", CelProfile.Condition, gate);
        var row = CelFixtures.Row(("qty", 3_000_000_000L));

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(compiled, row, previous: null, CelFixtures.Alice));
    }

    [Fact]
    public void A_null_argument_still_propagates_without_a_failure()
    {
        var calls = 0;

        Probe(CelValueType.Int, typeof(int), () => calls++).Invoke([null]).ShouldBeNull();
        calls.ShouldBe(0);
    }
}
