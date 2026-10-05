using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Ruling Q: on the fail-closed hook path a <b>present</b> operand a comparison or <c>!</c> cannot handle — a NaN,
/// infinite or out-of-range double, a string in a numeric field, a value of an unexpected CLR type — throws a function
/// failure instead of answering <see langword="false"/>, so it cannot quietly skip a reject. A null operand still answers
/// as it always did, and Rule and Access, which take the same comparison, do not move.
/// </summary>
public sealed class CelHookComparisonTests
{
    private static readonly EntitySchema _items = TestCelFunctions.Items with
    {
        Fields = [.. TestCelFunctions.Items.Fields, new FieldSchema { Name = "active", Type = FieldType.Boolean, Nullable = true }],
    };

    /// <summary>Operands no record the HTTP binder types can hold, but an embedded caller's own record can.</summary>
    public static TheoryData<object> Uncomparable => new()
    {
        double.NaN,
        double.PositiveInfinity,
        float.NegativeInfinity,
        1e30,
        "7",
        Guid.Empty,
    };

    /// <summary>The pinned reject case: a present NaN in <c>new.price &gt; 100</c> does not skip the reject.</summary>
    [Fact]
    public void A_present_nan_in_a_reject_condition_fails_closed_rather_than_reading_false() =>
        ShouldFailClosed(() => Condition("new.price > 100", ("price", double.NaN)), "_>_");

    /// <summary>The product's own evaluator, the one a before-hook gate calls, lets the failure escape too.</summary>
    [Fact]
    public void The_product_evaluator_lets_the_comparison_failure_escape() =>
        Should.Throw<CelFunctionException>(() => CelFixtures.Evaluator.Evaluate(
            Compile("new.price > 100", CelProfile.Condition), CelFixtures.Row(("price", double.NaN)), null, AlvoContext.Anonymous))
            .FunctionName.ShouldBe("_>_");

    [Theory]
    [MemberData(nameof(Uncomparable))]
    public void A_present_operand_that_cannot_be_compared_fails_a_condition_closed(object value) =>
        ShouldFailClosed(() => Condition("new.price > 100", ("price", value)), "_>_");

    [Theory]
    [InlineData("new.price == 100", "_==_")]
    [InlineData("new.price != 100", "_!=_")]
    [InlineData("new.price < 100", "_<_")]
    [InlineData("new.price <= 100", "_<=_")]
    [InlineData("new.price >= 100", "_>=_")]
    public void Every_comparison_operator_fails_closed_under_its_own_name(string source, string name) =>
        ShouldFailClosed(() => Condition(source, ("price", double.NaN)), name);

    [Fact]
    public void A_negated_comparison_over_an_uncomparable_operand_still_fails_closed() =>
        ShouldFailClosed(() => Condition("!(new.price > 100)", ("price", double.NaN)), "_>_");

    [Fact]
    public void A_date_that_is_no_instant_fails_closed() =>
        ShouldFailClosed(() => Condition("new.due < timestamp('2026-01-01T00:00:00Z')", ("due", "not-a-date")), "_<_");

    [Fact]
    public void A_uuid_that_is_no_uuid_fails_closed() =>
        ShouldFailClosed(() => Condition("new.ref_id == @user.id", ("ref_id", "not-a-uuid")), "_==_");

    [Theory]
    [InlineData("true")]
    [InlineData(1L)]
    public void A_present_non_bool_under_not_fails_closed(object value) =>
        ShouldFailClosed(() => Condition("!new.active", ("active", value)), "!_", "the operand is not a Bool");

    [Fact]
    public void Not_over_a_null_still_answers_true() => Condition("!new.active", ("active", null)).ShouldBeTrue();

    [Fact]
    public void Not_over_a_bool_answers_its_negation() => Condition("!new.active", ("active", false)).ShouldBeTrue();

    [Fact]
    public void A_null_operand_still_reads_false() => Condition("new.price > 100", ("price", null)).ShouldBeFalse();

    /// <summary>A double a decimal can hold is a number on the hook path, not a failure.</summary>
    [Fact]
    public void A_convertible_double_compares_without_failing() => Condition("new.price > 2", ("price", 2.5d)).ShouldBeTrue();

    /// <summary>A double a decimal can hold takes the decimal path through arithmetic and answers a decimal.</summary>
    [Fact]
    public void A_convertible_double_in_arithmetic_answers_a_decimal() =>
        Mutate("qty * 2", ("qty", 2.5d)).ShouldBeOfType<decimal>().ShouldBe(5.0m);

    /// <summary>The gate is the hook path's: a Rule reading the same operand still answers false, as it always did.</summary>
    [Theory]
    [MemberData(nameof(Uncomparable))]
    public void A_rule_over_the_same_operand_still_answers_false(object value) =>
        CelInterpreter.EvaluatePredicate(Compile("price > 100", CelProfile.Rule), CelFixtures.Row(("price", value)), previous: null, AlvoContext.Anonymous)
            .ShouldBeFalse();

    [Fact]
    public void A_rule_not_over_a_non_bool_still_answers_true() =>
        CelInterpreter.EvaluatePredicate(Compile("!active", CelProfile.Rule), CelFixtures.Row(("active", "true")), previous: null, AlvoContext.Anonymous)
            .ShouldBeTrue();

    private static CompiledExpression Compile(string source, CelProfile profile)
    {
        var result = TestCelFunctions.Compiler().Compile(source, profile, _items);
        return result.Expression ?? throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, AlvoContext.Anonymous);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(
            Compile(source, CelProfile.Mutate),
            CelFixtures.Row(row), previous: null, DateTimeOffset.UnixEpoch);

    private static void ShouldFailClosed(Action evaluate, string name, string reason = "the operands cannot be compared")
    {
        var failure = Should.Throw<CelFunctionException>(evaluate);

        failure.FunctionName.ShouldBe(name);
        failure.Reason.ShouldBe(reason);
    }
}
