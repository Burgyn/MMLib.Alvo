using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The interpreter's value semantics and its fail-closed directions: which operand a comparison is
/// normalized through before it is answered, which comparisons include equality, what an arithmetic
/// expression does with a zero divisor or an overflow, and what every entry point does with an argument
/// it cannot work with or an evaluation that throws. Each of these decides a row, not a message.
/// </summary>
public class CelInterpreterValueSemanticsTests
{
    [Fact]
    public void A_null_expression_is_refused_rather_than_collapsing_into_the_fail_closed_catch()
        => Should.Throw<ArgumentNullException>(
            () => CelInterpreter.EvaluatePredicate(null!, AlvoRecord.Empty, null, CelFixtures.Alice));

    /// <summary>
    /// The context is guarded at the entry point, not where it is first read: a predicate that never
    /// mentions <c>@user</c>/<c>@tenant</c> would otherwise answer happily for no caller at all — and the
    /// catch below would turn the one that does mention it into a silent denial rather than a bug report.
    /// </summary>
    [Fact]
    public void A_null_context_is_refused_even_by_a_predicate_that_never_reads_it()
        => Should.Throw<ArgumentNullException>(
            () => CelInterpreter.EvaluatePredicate(CelFixtures.CompileRule("true"), AlvoRecord.Empty, null, null!));

    [Fact]
    public void A_mask_refuses_a_null_context_rather_than_masking_for_nobody()
        => Should.Throw<ArgumentNullException>(
            () => CelInterpreter.EvaluateMask(CelFixtures.CompileRule("true"), null!));

    [Fact]
    public void A_scalar_evaluation_refuses_a_null_expression()
        => Should.Throw<ArgumentNullException>(() => CelInterpreter.EvaluateScalar(null!, AlvoRecord.Empty));

    [Fact]
    public void A_mutation_refuses_a_null_row()
        => Should.Throw<ArgumentNullException>(() => CelInterpreter.EvaluateMutation(
            CelFixtures.CompileMutate("now()"), null!, null, DateTimeOffset.UnixEpoch));

    /// <summary>
    /// A predicate whose evaluation throws denies. Driven through the one construct that can still throw
    /// inside a tree — <c>in</c> against an operand that is not the role set — over a row whose stored value
    /// <em>is</em> a matching string list: without the guard the membership is answered from that value and
    /// the row is admitted, which is exactly what a second string-list context value would introduce.
    /// </summary>
    [Fact]
    public void A_predicate_that_throws_denies_instead_of_admitting_the_row()
    {
        string[] storedRoles = ["admin"];

        CelInterpreter.EvaluatePredicate(
            RuleExpression(MembershipAgainstAFieldValue),
            CelFixtures.Row(("payload", storedRoles)),
            null,
            CelFixtures.Admin).ShouldBeFalse();
    }

    /// <summary>
    /// The computed path fails the other way round but just as quietly: a generated column must never make
    /// a write crash, so an evaluation that throws yields <see langword="null"/> and lets the caller decide.
    /// </summary>
    [Fact]
    public void A_computed_expression_that_throws_yields_null_rather_than_failing_the_write()
        => CelInterpreter.EvaluateScalar(ComputedExpression(MembershipAgainstAFieldValue), AlvoRecord.Empty).ShouldBeNull();

    [Fact]
    public void A_mutation_that_throws_yields_null_rather_than_escaping_the_write_transaction()
        => CelInterpreter.EvaluateMutation(
            MutateExpression(MembershipAgainstAFieldValue), AlvoRecord.Empty, null, DateTimeOffset.UnixEpoch).ShouldBeNull();

    [Fact]
    public void A_ternary_takes_the_false_branch_when_its_condition_is_false()
        => EvaluateComputed("total > 100 ? 1 : 2", CelFixtures.Row(("total", 5m))).ShouldBe(2L);

    /// <summary>
    /// Equal values are where the four relational operators differ, and the difference is a row: a rule
    /// reading <c>total &gt;= limit</c> admits the row that sits exactly on the limit, <c>&gt;</c> does not.
    /// </summary>
    [Theory]
    [InlineData("total < 10", false)]
    [InlineData("total <= 10", true)]
    [InlineData("total > 10", false)]
    [InlineData("total >= 10", true)]
    public void A_relational_comparison_of_equal_values_answers_by_whether_the_operator_includes_equality(
        string source, bool expected)
        => Evaluate(source, CelFixtures.Row(("total", 10m))).ShouldBe(expected);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_boolean_comparison_answers_equality_not_difference(bool stored, bool expected)
        => Evaluate("is_public == true", CelFixtures.Row(("is_public", stored))).ShouldBe(expected);

    /// <summary>
    /// A uuid that arrives as text — which is how it arrives from JSON — still matches the caller's own id.
    /// The normalization runs on either side of the operator, and the side where the stored text faces a
    /// context <see cref="Guid"/> is the ownership check itself: without it every owner is denied their own row.
    /// </summary>
    [Fact]
    public void A_uuid_stored_as_text_still_matches_the_context_guid()
        => Evaluate(
            "owner_id == @user.id",
            CelFixtures.Row(("owner_id", CelFixtures.Alice.User.Value.ToString()))).ShouldBeTrue();

    /// <summary>
    /// A stored value that is not a timestamp at all collapses the comparison, in both directions: an
    /// <c>!=</c> that reported "different" from a pair the interpreter could not even compare would make a
    /// hook condition fire on unparseable data.
    /// </summary>
    [Fact]
    public void An_unparseable_timestamp_collapses_the_comparison_instead_of_comparing_raw_values()
        => Evaluate(
            "created_at != approved_at",
            CelFixtures.Row(
                ("created_at", "not-a-timestamp"),
                ("approved_at", DateTimeOffset.UnixEpoch))).ShouldBeFalse();

    /// <summary>
    /// A <see cref="DateTime"/> with no kind is read as UTC, never as the host's local time — the same
    /// stored row must answer the same way on every machine that evaluates the rule.
    /// </summary>
    /// <remarks>
    /// The two readings coincide on a host whose local time is UTC, so this fact is only decisive off one;
    /// it is still the right assertion, because the wrong reading is a wrong answer everywhere else.
    /// </remarks>
    [Fact]
    public void A_kindless_datetime_is_read_as_utc_rather_than_as_the_hosts_local_time()
        => Evaluate(
            "created_at == approved_at",
            CelFixtures.Row(
                ("created_at", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified)),
                ("approved_at", new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero)))).ShouldBeTrue();

    /// <summary>
    /// A stored number no decimal can hold is not quietly read as zero: a rule gating on an amount would
    /// otherwise answer about a value the row never held.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e30)]
    public void A_double_no_decimal_can_hold_never_compares_equal_to_zero(double stored)
        => Evaluate("total == 0", CelFixtures.Row(("total", stored))).ShouldBeFalse();

    /// <summary>
    /// Only division guards its right operand against zero. A zero on the right of <c>+</c>, <c>-</c> or
    /// <c>*</c> is an ordinary operand, and refusing it would make every <c>x + 0</c> computed column null.
    /// </summary>
    [Theory]
    [InlineData("total + 0", 5)]
    [InlineData("total - 0", 5)]
    [InlineData("total * 0", 0)]
    public void A_zero_right_operand_is_a_guard_for_division_only(string source, double expected)
        => EvaluateComputed(source, CelFixtures.Row(("total", 5m))).ShouldBe((decimal)expected);

    [Fact]
    public void A_division_by_zero_yields_null_rather_than_throwing()
        => EvaluateComputed("total / 0", CelFixtures.Row(("total", 5m))).ShouldBeNull();

    /// <summary>
    /// Each arithmetic operator applies its own operation — pinned with operands whose product and
    /// quotient differ, so no two operators can be swapped without a failure here.
    /// </summary>
    [Theory]
    [InlineData("total + 2", 7)]
    [InlineData("total - 2", 3)]
    [InlineData("total * 2", 10)]
    [InlineData("total / 2", 2.5)]
    public void Each_arithmetic_operator_applies_its_own_operation(string source, double expected)
        => EvaluateComputed(source, CelFixtures.Row(("total", 5m))).ShouldBe((decimal)expected);

    [Fact]
    public void An_arithmetic_overflow_yields_null_rather_than_failing_the_write()
        => EvaluateComputed("total * total", CelFixtures.Row(("total", decimal.MaxValue))).ShouldBeNull();

    private static CelBinary MembershipAgainstAFieldValue => new(
        CelBinaryOperator.In,
        new CelLiteral(CelValueType.String, "admin"),
        new CelFieldRef("payload", CelValueType.StringList, CelRecordState.Current));

    private static bool Evaluate(string source, AlvoRecord row) =>
        CelInterpreter.EvaluatePredicate(CelFixtures.CompileRule(source), row, null, CelFixtures.Alice);

    private static object? EvaluateComputed(string source, AlvoRecord row) =>
        CelInterpreter.EvaluateScalar(CelFixtures.CompileComputed(source), row);

    private static CompiledExpression RuleExpression(CelNode root) =>
        new(root, CelProfile.Rule, CelValueType.Bool, "hand-built tree", CelFixtures.Orders);

    private static CompiledExpression ComputedExpression(CelNode root) =>
        new(root, CelProfile.Computed, CelValueType.Bool, "hand-built tree", CelFixtures.Orders);

    private static CompiledExpression MutateExpression(CelNode root) =>
        new(root, CelProfile.Mutate, CelValueType.Bool, "hand-built tree", CelFixtures.Orders);
}
