using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Ruling Q: on the fail-closed hook path a <b>present</b> operand a comparison or <c>!</c> cannot handle — a NaN,
/// infinite or out-of-range double, a string in a numeric field, a value of an unexpected CLR type — throws a function
/// failure instead of answering <see langword="false"/>, so it cannot quietly skip a reject. Ruling R extends it to every
/// other Boolean position: either side of <c>&amp;&amp;</c> and <c>||</c>, a ternary's condition, and the condition's own value.
/// A null operand still answers as it always did, and Rule and Access, which take the same comparison, do not move.
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

    /// <summary>
    /// Every uncomparable operand under each profile that keeps the old answer: Rule, and Access. An Access level has
    /// no row to read, so its row is the Rule-compiled tree re-labelled — which is exactly the claim: the gate reads
    /// the profile and nothing else.
    /// </summary>
    public static TheoryData<CelProfile, object> UncomparableOffTheHookPath
    {
        get
        {
            var rows = new TheoryData<CelProfile, object>();
            foreach (var profile in new[] { CelProfile.Rule, CelProfile.Access })
            {
                foreach (var value in Uncomparable)
                {
                    rows.Add(profile, value.Data);
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// The gate is the hook path's: a Rule or an Access level reading the same operand still answers false, as it
    /// always did (Ruling Q's scope).
    /// </summary>
    [Theory]
    [MemberData(nameof(UncomparableOffTheHookPath))]
    public void A_rule_or_access_over_the_same_operand_still_answers_false(CelProfile profile, object value) =>
        CelInterpreter.EvaluatePredicate(Relabel(Compile("price > 100", CelProfile.Rule), profile), CelFixtures.Row(("price", value)), previous: null, AlvoContext.Anonymous)
            .ShouldBeFalse();

    /// <summary>
    /// Rule's <c>!</c> did not move either — and its <see langword="true"/> is the grant direction, an embedded-only
    /// residual tracked as #324, not a safe answer.
    /// </summary>
    [Fact]
    public void A_rule_not_over_a_non_bool_still_answers_true() =>
        CelInterpreter.EvaluatePredicate(Compile("!active", CelProfile.Rule), CelFixtures.Row(("active", "true")), previous: null, AlvoContext.Anonymous)
            .ShouldBeTrue();

    /// <summary>Ruling R: each Boolean position of the hook path, with a present operand that is no Bool.</summary>
    /// <remarks>
    /// <c>!</c> was Ruling Q's; these are the remaining places a value is read as a truth value. Without them a reject
    /// written <c>new.active &amp;&amp; …</c>, <c>new.active ? … : …</c> or plain <c>new.active</c> reads a present
    /// <c>"true"</c> as <see langword="false"/> and lets the write through.
    /// </remarks>
    [Theory]
    [InlineData("new.active && true", "_&&_")]
    [InlineData("true && new.active", "_&&_")]
    [InlineData("new.active || false", "_||_")]
    [InlineData("false || new.active", "_||_")]
    public void A_present_non_bool_in_a_boolean_position_fails_a_condition_closed(string source, string name)
    {
        ShouldFailClosed(() => Condition(source, ("active", "true")), name, "the operand is not a Bool");
        ShouldFailClosed(() => Condition(source, ("active", 1L)), name, "the operand is not a Bool");
    }

    /// <summary>A bare field as the whole condition is the last Boolean position: the condition's own value.</summary>
    /// <remarks>
    /// No operator is at fault, so the failure is named <c>&lt;condition&gt;</c>, a token no CEL identifier can be: a
    /// plain <c>condition</c> would read as a function of that name in "The CEL function '…' failed", and could collide
    /// with one a host registers.
    /// </remarks>
    [Theory]
    [InlineData("true")]
    [InlineData(1L)]
    public void A_present_non_bool_as_the_whole_condition_fails_closed(object value) =>
        ShouldFailClosed(
            () => Condition("new.active", ("active", value)),
            "<condition>",
            "the hook's condition evaluated to a present value that is not a Bool");

    /// <summary>
    /// A ternary's condition, on either hook profile. Defensive: the ternary compiles in <see cref="CelProfile.Computed"/>
    /// only today, so the tree is compiled there and re-labelled — the interpreter must not depend on the type checker
    /// keeping it out of the hook path.
    /// </summary>
    [Theory]
    [InlineData(CelProfile.Condition)]
    [InlineData(CelProfile.Mutate)]
    public void A_present_non_bool_ternary_condition_fails_the_hook_path_closed(CelProfile profile) =>
        ShouldFailClosed(() => Ternary(profile, "true"), "_?_:_", "the operand is not a Bool");

    /// <summary>The ternary off the fail-closed path — Computed, and Rule re-labelled — reads it as false, as before.</summary>
    [Fact]
    public void A_ternary_off_the_hook_path_did_not_move()
    {
        Ternary(CelProfile.Computed, "true").ShouldBe("off");

        // The Rule row enters through EvaluateMutation, the only entry point that returns a branch's value (a Rule's own,
        // EvaluatePredicate, answers a Bool, and 'on'/'off' is not one). That is sound because the gate reads only the
        // expression's profile (CelHookArithmetic.FailsClosed), never the entry point: Rule is off the hook path here too.
        Ternary(CelProfile.Rule, "true").ShouldBe("off");
    }

    /// <summary>On the hook path a null or a real Bool still picks its branch as it always did.</summary>
    [Fact]
    public void A_ternary_over_a_null_or_a_bool_picks_its_branch_as_before()
    {
        Ternary(CelProfile.Mutate, null).ShouldBe("off");
        Ternary(CelProfile.Mutate, true).ShouldBe("on");
    }

    /// <summary>Short-circuit stays: a real Bool that decides the answer never evaluates the other side.</summary>
    [Theory]
    [InlineData("false && new.active", false)]
    [InlineData("true || new.active", true)]
    public void A_deciding_bool_still_short_circuits_past_a_non_bool(string source, bool expected) =>
        Condition(source, ("active", "true")).ShouldBe(expected);

    /// <summary>Null propagates as before: it reads false in every Boolean position, and never throws.</summary>
    [Theory]
    [InlineData("new.active && true", false)]
    [InlineData("new.active || true", true)]
    [InlineData("new.active", false)]
    public void A_null_in_a_boolean_position_reads_false_as_before(string source, bool expected) =>
        Condition(source, ("active", null)).ShouldBe(expected);

    /// <summary>Real Bools answer as they always did.</summary>
    [Theory]
    [InlineData("new.active && true", true)]
    [InlineData("new.active || false", true)]
    [InlineData("new.active", true)]
    public void A_bool_in_a_boolean_position_answers_as_before(string source, bool expected) =>
        Condition(source, ("active", true)).ShouldBe(expected);

    /// <summary>
    /// Rule and Access did not move: the same shapes over the same present non-Bool still read it as
    /// <see langword="false"/> — Ruling R, like Ruling Q, is the hook path's alone. The Access rows are the Rule tree
    /// re-labelled, as in <see cref="UncomparableOffTheHookPath"/>.
    /// </summary>
    [Theory]
    [InlineData(CelProfile.Rule, "active && true")]
    [InlineData(CelProfile.Rule, "true && active")]
    [InlineData(CelProfile.Rule, "active || false")]
    [InlineData(CelProfile.Rule, "false || active")]
    [InlineData(CelProfile.Rule, "active")]
    [InlineData(CelProfile.Access, "active && true")]
    [InlineData(CelProfile.Access, "true && active")]
    [InlineData(CelProfile.Access, "active || false")]
    [InlineData(CelProfile.Access, "false || active")]
    [InlineData(CelProfile.Access, "active")]
    public void A_rule_or_access_over_a_non_bool_in_a_boolean_position_did_not_move(CelProfile profile, string source) =>
        CelInterpreter.EvaluatePredicate(
            Relabel(Compile(source, CelProfile.Rule), profile), CelFixtures.Row(("active", "true")), previous: null, AlvoContext.Anonymous)
            .ShouldBeFalse();

    /// <summary>Old and new values <c>changed(f)</c> cannot compare: present, and of no kind a comparison normalises.</summary>
    public static TheoryData<object> UncomparablePairs => new()
    {
        double.NaN,
        double.PositiveInfinity,
        1e30,
        new object(),
    };

    /// <summary>
    /// <c>changed(f)</c> over two present values it cannot compare used to read as changed, so <c>!changed(f)</c> was
    /// <see langword="false"/> — a reject gated on it never fired (final review M2). On the hook path it fails closed.
    /// </summary>
    [Theory]
    [MemberData(nameof(UncomparablePairs))]
    public void Changed_over_an_uncomparable_pair_fails_closed(object value) =>
        ShouldFailClosed(
            () => ConditionOnUpdate("!changed(price)", ("price", value), ("price", value)),
            "changed",
            "the old and new values cannot be compared");

    /// <summary>A null on either side is still an ordinary answer: null to a value is a change, null to null is not.</summary>
    [Fact]
    public void Changed_over_a_null_still_answers()
    {
        ConditionOnUpdate("changed(price)", ("price", null), ("price", double.NaN)).ShouldBeTrue();
        ConditionOnUpdate("changed(price)", ("price", null), ("price", null)).ShouldBeFalse();
    }

    /// <summary><c>active ? 'on' : 'off'</c>, compiled where a ternary compiles and evaluated as <paramref name="profile"/>.</summary>
    private static object? Ternary(CelProfile profile, object? active)
    {
        var expression = Relabel(Compile("active ? 'on' : 'off'", CelProfile.Computed), profile);
        var row = CelFixtures.Row(("active", active));
        return profile == CelProfile.Computed
            ? CelInterpreter.EvaluateScalar(expression, row)
            : CelInterpreter.EvaluateMutation(expression, row, previous: null, DateTimeOffset.UnixEpoch);
    }

    private static CompiledExpression Relabel(CompiledExpression expression, CelProfile profile) =>
        new(expression.Root, profile, expression.ResultType, expression.Source, expression.Entity);

    private static CompiledExpression Compile(string source, CelProfile profile)
    {
        var result = TestCelFunctions.Compiler().Compile(source, profile, _items);
        return result.Expression ?? throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, AlvoContext.Anonymous);

    private static bool ConditionOnUpdate(string source, (string Field, object? Value) old, (string Field, object? Value) current) =>
        CelInterpreter.EvaluatePredicate(
            Compile(source, CelProfile.Condition), CelFixtures.Row(current), CelFixtures.Row(old), AlvoContext.Anonymous);

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
