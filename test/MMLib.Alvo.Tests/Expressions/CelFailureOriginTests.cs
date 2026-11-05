using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// E1's line, decided by whose data was refused: a built-in or the interpreter refusing a value the caller sent is theirs
/// to fix (422); anything a host function did or produced, the stored row, a value a hook wrote, and Alvo's own defect are
/// not (500).
/// </summary>
public sealed class CelFailureOriginTests
{
    [Fact]
    public void A_built_in_s_refusal_is_on_the_caller_s_data() =>
        Should.Throw<CelFunctionException>(() => Builtin("int").Invoke(["x"])).OnCallerData.ShouldBeTrue();

    [Fact]
    public void Hook_arithmetic_is_on_the_caller_s_data() =>
        Should.Throw<CelFunctionException>(() => CelHookArithmetic.Apply(CelBinaryOperator.Divide, 1L, 0L)).OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_whole_condition_with_no_bool_is_on_the_caller_s_data() =>
        new CelFunctionException(CelInterpreter.WholeCondition, CelInterpreter.WholeConditionReason).OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_built_in_argument_that_does_not_fit_is_on_the_caller_s_data() =>
        new CelFunctionException("substring", isHost: false, "an argument does not fit parameter 'start' (Int32)").OnCallerData.ShouldBeTrue();

    [Fact]
    public void A_host_function_s_throw_is_not() =>
        Should.Throw<CelFunctionException>(() => HostCelFunction.Create("boom", string (string s) => throw new FormatException(), summary: null).Invoke(["x"]))
            .OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_host_function_s_argument_that_does_not_fit_is_not() =>
        new CelFunctionException("rate", isHost: true, "an argument does not fit parameter 'n' (Int32)").OnCallerData.ShouldBeFalse();

    [Fact]
    public void The_interpreter_s_defect_is_not() =>
        new CelFunctionException(CelInterpreter.HookEvaluation, CelInterpreter.HookEvaluationReason, new InvalidCastException()).OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_built_in_body_that_threw_unexpectedly_is_not() =>
        new CelFunctionException("math.round", isHost: false, new ArgumentOutOfRangeException()).OnCallerData.ShouldBeFalse();

    [Fact]
    public void A_built_in_refusing_the_caller_s_own_value_is_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeTrue();
    }

    [Fact]
    public void A_built_in_refusing_a_host_function_s_output_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(echo(new.name)) > 1", CelProfile.Condition, TestCelFunctions.Echo);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void Arithmetic_over_a_host_function_s_output_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("new.qty / half(new.price) > 1", CelProfile.Condition, TestCelFunctions.Half);
        var row = new AlvoRecord(new Dictionary<string, object?> { ["qty"] = 4L, ["price"] = 0m });

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, null, _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_built_in_refusing_the_stored_row_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(old.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("12"), Row("x"), _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void An_inner_refusal_is_judged_by_its_own_subtree_not_by_a_host_call_beside_it()
    {
        var condition = TestCelFunctions.Compile("echo(new.name) == 'a' || int(new.name) > 1", CelProfile.Condition, TestCelFunctions.Echo);

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)).OnCallerData.ShouldBeTrue();
    }

    [Fact]
    public void A_value_an_earlier_hook_wrote_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelDataOrigin.Attributed(
                condition, new HashSet<string>(StringComparer.Ordinal) { "name" },
                () => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)))
            .OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_value_no_hook_wrote_stays_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("int(new.name) > 1", CelProfile.Condition);

        Should.Throw<CelFunctionException>(() => CelDataOrigin.Attributed(
                condition, new HashSet<string>(StringComparer.Ordinal) { "qty" },
                () => CelInterpreter.EvaluatePredicate(condition, Row("x"), null, _caller)))
            .OnCallerData.ShouldBeTrue();
    }

    /// <summary>A whole condition that is no Bool is judged by the whole tree (Ruling 38): over the stored row it stays 500.</summary>
    [Fact]
    public void A_whole_condition_over_the_stored_row_with_no_bool_is_not_the_caller_s()
    {
        var condition = Flagged("old.flag");

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Flag(true), Flag("yes"), _caller)).OnCallerData.ShouldBeFalse();
    }

    [Fact]
    public void A_whole_condition_over_the_caller_s_row_with_no_bool_is_the_caller_s()
    {
        var condition = Flagged("new.flag");

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Flag("yes"), Flag(true), _caller)).OnCallerData.ShouldBeTrue();
    }

    /// <summary>
    /// Ruling 44: a refusal records which of the written row's fields its refusing subtree reads, so the Data API can
    /// answer 422 only when the caller sent every one of them.
    /// </summary>
    [Fact]
    public void A_refusal_records_the_fields_its_refusing_subtree_reads()
    {
        var condition = TestCelFunctions.Compile("new.qty > 0 || int(new.name) > 1", CelProfile.Condition);
        var row = new AlvoRecord(new Dictionary<string, object?> { ["qty"] = 0L, ["name"] = "x" });

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, null, _caller))
            .ReadFields.ShouldBe(["name"], ignoreOrder: true);
    }

    /// <summary>A literal a direct-argument check cannot see (<c>int('x')</c> itself is refused at apply).</summary>
    [Fact]
    public void A_refusal_of_a_literal_reads_no_field() =>
        RefusalOf("int(substring('xy', 0, 1)) > 1").ReadFields.ShouldBeEmpty();

    [Fact]
    public void A_refusal_of_the_caller_s_identity_reads_no_field() =>
        RefusalOf("int(string(@user.id)) > 1").ReadFields.ShouldBeEmpty();

    [Fact]
    public void A_whole_condition_with_no_bool_records_what_it_reads()
    {
        var condition = Flagged("new.flag");

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, Flag("yes"), Flag(true), _caller))
            .ReadFields.ShouldBe(["flag"]);
    }

    /// <summary>A refusal nothing judged reads nothing anyone could have sent.</summary>
    [Fact]
    public void An_unjudged_refusal_reads_no_field() =>
        new CelFunctionException("int", "the text is not a whole number such as 42 or -7").ReadFields.ShouldBeEmpty();

    /// <summary>
    /// A <c>changed</c> that cannot compare refuses the stored row as much as the request (Ruling 26): never the caller's.
    /// </summary>
    [Fact]
    public void A_changed_that_cannot_compare_is_not_the_caller_s()
    {
        var condition = TestCelFunctions.Compile("!changed(name)", CelProfile.Condition);
        var row = new AlvoRecord(new Dictionary<string, object?> { ["name"] = new object() });

        Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, row, _caller)).OnCallerData.ShouldBeFalse();
    }

    /// <summary>A hook writing <c>name</c> says nothing about <c>old.name</c>, the stored row's value (Ruling 26(b)).</summary>
    [Fact]
    public void Reading_the_stored_value_of_a_hook_written_field_is_not_reading_the_hook_s_write() =>
        CelDataOrigin.Reads(
                TestCelFunctions.Compile("int(old.name) > 1", CelProfile.Condition).Root, new HashSet<string>(StringComparer.Ordinal) { "name" })
            .ShouldBeFalse();

    /// <summary>A re-attributed refusal keeps the original as its inner exception, so the log carries the real stack (m4).</summary>
    [Fact]
    public void A_re_attributed_refusal_keeps_the_original_as_its_inner_exception()
    {
        var original = new CelFunctionException("int", "the text is not a whole number such as 42 or -7");

        original.NotTheCallers().InnerException.ShouldBeSameAs(original);
    }

    private static CelFunctionException RefusalOf(string source) =>
        Should.Throw<CelFunctionException>(
            () => CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition), Row("12"), null, _caller));

    private static CompiledExpression Flagged(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, new MMLib.Alvo.Schema.EntitySchema
        {
            Name = "flags",
            Fields = [new MMLib.Alvo.Schema.FieldSchema { Name = "flag", Type = MMLib.Alvo.Schema.FieldType.Boolean, Nullable = true }],
        }).Expression!;

    private static AlvoRecord Flag(object value) => new(new Dictionary<string, object?> { ["flag"] = value });

    private static readonly AlvoContext _caller = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    private static AlvoRecord Row(string name) => new(new Dictionary<string, object?> { ["name"] = name });

    private static CelFunction Builtin(string name) =>
        CelFunctionCatalog.BuiltIns.Overloads(name).First(function => function.Parameters.Count == 1 && function.Parameters[0].Type == CelValueType.String);
}
