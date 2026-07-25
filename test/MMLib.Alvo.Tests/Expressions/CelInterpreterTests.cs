using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions.Internal;
using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Tests.Expressions;

public class CelInterpreterTests
{
    private static AlvoRecord Row((string Field, object? Value)[] fields) => CelFixtures.Row(fields);

    private static bool Evaluate(string source, AlvoRecord row, AlvoContext context) =>
        CelInterpreter.EvaluatePredicate(CelFixtures.CompileRule(source), row, previous: null, context);

    private static bool EvaluateCondition(string source, AlvoRecord current, AlvoRecord? previous) =>
        CelInterpreter.EvaluatePredicate(CelFixtures.CompileCondition(source), current, previous, CelFixtures.Alice);

    private static object? EvaluateComputed(string source, AlvoRecord row) =>
        CelInterpreter.EvaluateScalar(CelFixtures.CompileComputed(source), row);

    [Fact]
    public void A_null_field_operand_makes_the_comparison_false()
    {
        Evaluate("owner_id == @user.id", Row([("owner_id", null)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void Negation_sees_the_collapsed_comparison_so_a_null_owner_is_allowed()
    {
        Evaluate("!(owner_id == @user.id)", Row([("owner_id", null)]), CelFixtures.Alice).ShouldBeTrue();
    }

    [Fact]
    public void Role_membership_reads_the_context_role_set()
    {
        Evaluate("'editor' in @user.roles", AlvoRecord.Empty, CelFixtures.Editor).ShouldBeTrue();
        Evaluate("'editor' in @user.roles", AlvoRecord.Empty, CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void Tenant_reference_reads_the_context_tenant()
    {
        Evaluate("tenant_id == @tenant.id", Row([("tenant_id", CelFixtures.AcmeTenant.Value)]), CelFixtures.AcmeUser).ShouldBeTrue();
        Evaluate("tenant_id == @tenant.id", Row([("tenant_id", CelFixtures.AcmeTenant.Value)]), CelFixtures.OtherTenantUser).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_tenant_in_the_context_denies()
    {
        Evaluate("tenant_id == @tenant.id", Row([("tenant_id", CelFixtures.AcmeTenant.Value)]), CelFixtures.TenantlessAlice).ShouldBeFalse();
    }

    [Fact]
    public void Or_absorbs_a_failing_branch()
    {
        Evaluate("'admin' in @user.roles || owner_id == @user.id", Row([("owner_id", null)]), CelFixtures.Admin).ShouldBeTrue();
    }

    [Fact]
    public void And_short_circuits_on_a_false_left_operand()
    {
        Evaluate("owner_id == @user.id && 'admin' in @user.roles", Row([("owner_id", null)]), CelFixtures.Admin).ShouldBeFalse();
    }

    [Fact]
    public void Or_genuinely_short_circuits_and_never_reads_the_right_operands_field()
    {
        var poisoned = new AlvoRecord(new ThrowingFieldSource());

        Evaluate("'admin' in @user.roles || owner_id == @user.id", poisoned, CelFixtures.Admin).ShouldBeTrue();
    }

    /// <summary>
    /// An <see cref="IReadOnlyDictionary{TKey,TValue}"/> that throws from every member. Used only
    /// to prove <c>||</c> genuinely skips evaluating its right operand rather than evaluating it
    /// and relying on the entry-point's catch-all to mask the throw as <see langword="false"/> —
    /// a non-short-circuiting implementation would observe the throw and this test would fail.
    /// </summary>
    private sealed class ThrowingFieldSource : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException("A short-circuited operand must never be read.");

        public IEnumerable<string> Keys => throw new InvalidOperationException("A short-circuited operand must never be read.");

        public IEnumerable<object?> Values => throw new InvalidOperationException("A short-circuited operand must never be read.");

        public int Count => throw new InvalidOperationException("A short-circuited operand must never be read.");

        public bool ContainsKey(string key) => throw new InvalidOperationException("A short-circuited operand must never be read.");

        public bool TryGetValue(string key, out object? value) =>
            throw new InvalidOperationException("A short-circuited operand must never be read.");

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            throw new InvalidOperationException("A short-circuited operand must never be read.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Changed_is_false_on_create_and_true_at_a_transition()
    {
        EvaluateCondition("changed(status)", Row([("status", "approved")]), previous: null).ShouldBeFalse();
        EvaluateCondition("changed(status)", Row([("status", "approved")]), Row([("status", "draft")])).ShouldBeTrue();
        EvaluateCondition("changed(status)", Row([("status", "approved")]), Row([("status", "approved")])).ShouldBeFalse();
    }

    [Fact]
    public void Changed_treats_null_versus_null_as_unchanged()
    {
        EvaluateCondition("changed(owner_id)", Row([("owner_id", null)]), Row([("owner_id", null)])).ShouldBeFalse();
        EvaluateCondition("changed(owner_id)", Row([("owner_id", Guid.NewGuid())]), Row([("owner_id", null)])).ShouldBeTrue();
    }

    [Fact]
    public void New_and_old_read_the_two_images()
    {
        EvaluateCondition(
            "changed(status) && new.status == 'approved'", Row([("status", "approved")]), Row([("status", "draft")])).ShouldBeTrue();
        EvaluateCondition("old.status == 'draft'", Row([("status", "approved")]), Row([("status", "draft")])).ShouldBeTrue();
    }

    /// <summary>
    /// Pins the contract on <c>current</c>: it must be the complete post-image. A guard like
    /// <c>!changed(tenant_id)</c> must not deny an ordinary update just because some OTHER field
    /// changed, as long as the full post-image still repeats the same <c>tenant_id</c>.
    /// </summary>
    [Fact]
    public void Changed_is_false_for_an_unchanged_field_in_a_complete_post_image()
    {
        var tenantId = CelFixtures.AcmeTenant.Value;

        EvaluateCondition(
            "changed(tenant_id)",
            Row([("tenant_id", tenantId), ("status", "approved")]),
            Row([("tenant_id", tenantId), ("status", "draft")])).ShouldBeFalse();
    }

    [Fact]
    public void Has_is_false_for_an_absent_field_and_true_for_a_present_value()
    {
        Evaluate("has(owner_id)", Row([]), CelFixtures.Alice).ShouldBeFalse();
        Evaluate("has(owner_id)", Row([("owner_id", Guid.NewGuid())]), CelFixtures.Alice).ShouldBeTrue();
    }

    [Fact]
    public void Has_is_false_for_a_present_null_field()
    {
        Evaluate("has(owner_id)", Row([("owner_id", null)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void Has_is_false_for_a_dbnull_field_matching_sql_null_semantics()
    {
        Evaluate("has(owner_id)", Row([("owner_id", DBNull.Value)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void Not_has_is_true_exactly_when_has_is_false()
    {
        Evaluate("!has(owner_id)", Row([]), CelFixtures.Alice).ShouldBeTrue();
        Evaluate("!has(owner_id)", Row([("owner_id", null)]), CelFixtures.Alice).ShouldBeTrue();
        Evaluate("!has(owner_id)", Row([("owner_id", DBNull.Value)]), CelFixtures.Alice).ShouldBeTrue();
        Evaluate("!has(owner_id)", Row([("owner_id", Guid.NewGuid())]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void In_is_false_when_the_left_operand_is_null()
    {
        Evaluate("title in @user.roles", Row([("title", null)]), CelFixtures.Editor).ShouldBeFalse();
    }

    [Fact]
    public void In_is_false_when_the_left_operand_is_not_a_string()
    {
        Evaluate("title in @user.roles", Row([("title", 42)]), CelFixtures.Editor).ShouldBeFalse();
    }

    [Fact]
    public void Numeric_comparison_widens_int_to_decimal()
    {
        Evaluate("total > 5", Row([("total", 10.5m)]), CelFixtures.Alice).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NumericWideningCases))]
    public void Numeric_comparison_widens_across_every_clr_numeric_type(object total, bool expected)
    {
        Evaluate("total > 5", Row([("total", total)]), CelFixtures.Alice).ShouldBe(expected);
    }

    public static TheoryData<object, bool> NumericWideningCases() => new()
    {
        { 10, true },
        { 10L, true },
        { (short)10, true },
        { (byte)10, true },
        { (sbyte)10, true },
        { (ushort)10, true },
        { 10u, true },
        { 10ul, true },
        { 10.5m, true },
        { 10.5d, true },
        { 10.5f, true },
        { 3, false },
        { 3L, false },
        { (short)3, false },
        { (byte)3, false },
        { (sbyte)3, false },
        { (ushort)3, false },
        { 3u, false },
        { 3ul, false },
        { 3.0d, false },
    };

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void An_out_of_range_double_fails_the_comparison_instead_of_throwing(double total)
    {
        Evaluate("total > 5", Row([("total", total)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void The_double_nearest_decimal_max_value_fails_the_comparison_instead_of_throwing()
    {
        Evaluate("total > 5", Row([("total", (double)decimal.MaxValue)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void The_double_nearest_decimal_min_value_fails_the_comparison_instead_of_throwing()
    {
        Evaluate("total > 5", Row([("total", (double)decimal.MinValue)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void A_float_beyond_decimals_range_fails_the_comparison_instead_of_throwing()
    {
        Evaluate("total > 5", Row([("total", 7.9228163e28f)]), CelFixtures.Alice).ShouldBeFalse();
    }

    [Fact]
    public void A_guid_field_compares_equal_to_its_string_representation()
    {
        var id = Guid.NewGuid();

        Evaluate("id == owner_id", Row([("id", id), ("owner_id", id.ToString())]), CelFixtures.Alice).ShouldBeTrue();
    }

    [Fact]
    public void A_timestamp_compares_equal_across_offset_datetime_and_string()
    {
        var instant = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var asDateTime = instant.UtcDateTime;
        var asString = instant.ToString("O", CultureInfo.InvariantCulture);

        Evaluate(
            "created_at == approved_at",
            Row([("created_at", instant), ("approved_at", asDateTime)]),
            CelFixtures.Alice).ShouldBeTrue();

        Evaluate(
            "created_at == approved_at",
            Row([("created_at", instant), ("approved_at", asString)]),
            CelFixtures.Alice).ShouldBeTrue();
    }

    [Fact]
    public void Changed_normalizes_a_timestamp_field_stored_as_a_string_before_comparing()
    {
        var instant = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var asString = instant.ToString("O", CultureInfo.InvariantCulture);

        EvaluateCondition("changed(created_at)", Row([("created_at", instant)]), Row([("created_at", asString)])).ShouldBeFalse();
    }

    /// <summary>
    /// Asserts through the negation, not the bare comparison: a bare <c>==</c> reads
    /// <see langword="false"/> whether the comparison correctly collapsed (fails closed by
    /// construction) or an exception was thrown and swallowed by the entry-point's catch-all —
    /// the two cases are indistinguishable from a bare <c>==</c> alone. <c>!(...)</c> discriminates
    /// them: a real collapse yields <see langword="true"/> here, while a swallowed throw still
    /// yields <see langword="false"/> (the catch-all returns <see langword="false"/>
    /// unconditionally, regardless of where in the tree the throw happened).
    /// </summary>
    [Theory]
    [MemberData(nameof(NonThrowingWeirdValueCases))]
    public void An_unexpected_clr_type_never_matches_the_caller_even_under_negation(object? weirdValue)
    {
        Evaluate("!(owner_id == @user.id)", Row([("owner_id", weirdValue)]), CelFixtures.Alice).ShouldBeTrue();
    }

    public static TheoryData<object?> NonThrowingWeirdValueCases()
    {
        var data = new TheoryData<object?>
        {
            JsonDocument.Parse("{\"a\":1}").RootElement,
            JsonDocument.Parse("[1,2,3]").RootElement,
            new Dictionary<string, object?> { ["nested"] = "value" },
            new List<object?> { 1, 2, 3 },
            new object?[] { 1, "two", null },
            DBNull.Value,
            'x',
        };
        return data;
    }

    [Fact]
    public void Evaluate_scalar_computes_arithmetic_across_widened_numeric_types()
    {
        EvaluateComputed("total + total", Row([("total", 2.5m)])).ShouldBe(5.0m);
    }

    [Fact]
    public void Evaluate_scalar_yields_null_for_arithmetic_on_a_null_operand()
    {
        EvaluateComputed("total + total", Row([("total", null)])).ShouldBeNull();
    }

    [Fact]
    public void Evaluate_scalar_yields_null_for_division_by_zero_instead_of_throwing()
    {
        EvaluateComputed("total / (total - total)", Row([("total", 5m)])).ShouldBeNull();
    }

    [Fact]
    public void Evaluate_scalar_negates_a_numeric_field()
    {
        EvaluateComputed("-total", Row([("total", 3m)])).ShouldBe(-3.0m);
    }

    [Fact]
    public void No_exception_escapes_evaluate_predicate_for_a_malformed_expression_input()
    {
        var expression = CelFixtures.CompileRule("owner_id == @user.id");

        var result = CelInterpreter.EvaluatePredicate(
            expression, Row([("owner_id", new object())]), previous: null, CelFixtures.Alice);

        result.ShouldBeFalse();
    }

    [Fact]
    public void Alvo_record_treats_an_absent_field_the_same_as_a_present_null()
    {
        var record = Row([("status", "draft")]);

        record["owner_id"].ShouldBeNull();
        record.TryGetValue("owner_id", out var value).ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public void Alvo_record_with_returns_a_copy_with_the_field_added()
    {
        var updated = AlvoRecord.Empty.With("status", "draft");

        updated["status"].ShouldBe("draft");
        AlvoRecord.Empty["status"].ShouldBeNull();
    }

    [Fact]
    public void Alvo_record_normalizes_a_dbnull_field_to_null()
    {
        var record = Row([("owner_id", DBNull.Value)]);

        record["owner_id"].ShouldBeNull();
        record.TryGetValue("owner_id", out var value).ShouldBeTrue();
        value.ShouldBeNull();
    }

    [Fact]
    public void Alvo_record_rejects_a_null_values_dictionary()
    {
        Should.Throw<ArgumentNullException>(() => new AlvoRecord(null!));
    }

    [Fact]
    public void Alvo_records_with_the_same_content_are_equal_regardless_of_key_order()
    {
        var first = Row([("owner_id", (object?)Guid.Empty), ("status", "draft")]);
        var second = Row([("status", "draft"), ("owner_id", (object?)Guid.Empty)]);

        first.ShouldBe(second);
        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Alvo_records_with_different_content_are_not_equal()
    {
        var first = Row([("status", "draft")]);
        var second = Row([("status", "approved")]);

        first.ShouldNotBe(second);
    }
}
