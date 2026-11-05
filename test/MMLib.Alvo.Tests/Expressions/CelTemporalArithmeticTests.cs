using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>CEL's timestamp/duration overloads, typed at compile time and evaluated with CEL's range errors (HX-11 Rulings 7, 9, 10, 12).</summary>
public sealed class CelTemporalArithmeticTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly EntitySchema _bookings = new()
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "due_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "hours", Type = FieldType.Integer, Nullable = true },
        ],
    };

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        CelFixtures.Compiler.Compile(source, profile, _bookings);

    [Theory]
    [InlineData("ends_at - starts_at <= duration('8h')", CelProfile.Rule)]
    [InlineData("starts_at + duration('1h') < ends_at", CelProfile.Rule)]
    [InlineData("duration('1h') + starts_at < ends_at", CelProfile.Rule)]
    [InlineData("ends_at - duration('30m') > starts_at", CelProfile.Rule)]
    [InlineData("duration('1h') + duration('30m') == duration('90m')", CelProfile.Rule)]
    [InlineData("new.ends_at - new.starts_at > duration('0')", CelProfile.Condition)]
    [InlineData("due_on + duration('24h') > starts_at", CelProfile.Rule)]
    public void Cel_s_overloads_compile_where_temporal_arithmetic_is_admitted(string source, CelProfile profile) =>
        Compile(source, profile).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("starts_at + duration('1h')", CelProfile.Mutate, CelValueType.Timestamp)]
    public void A_mutate_value_may_be_a_shifted_timestamp(string source, CelProfile profile, CelValueType type) =>
        Compile(source, profile).Expression!.ResultType.ShouldBe(type);

    [Theory]
    [InlineData("duration('1h') * 2 == duration('2h')", "'*'")]
    [InlineData("hours + duration('1h') > duration('0')", "number")]
    [InlineData("starts_at + starts_at > ends_at", "two timestamps")]
    [InlineData("-duration('1h') < duration('0')", "'-'")]
    [InlineData("ends_at - starts_at", "boolean")]
    public void Anything_outside_the_overload_set_is_refused(string source, string reason)
    {
        var result = Compile(source, CelProfile.Rule);
        result.IsSuccess.ShouldBeFalse();
        string.Join(" ", result.Errors.Select(error => error.Message)).ShouldContain(reason);
    }

    [Fact]
    public void A_duration_from_a_field_is_refused_because_duration_takes_a_constant()
    {
        var result = CelFixtures.Compiler.Compile("starts_at + duration(string(hours) + 'h') < ends_at", CelProfile.Condition, _bookings);
        result.Errors.ShouldContain(error => error.Message.Contains("constant", StringComparison.Ordinal));
    }

    [Fact]
    public void Numeric_arithmetic_stays_refused_in_a_rule() =>
        Compile("hours * 2 > 4", CelProfile.Rule).IsSuccess.ShouldBeFalse();

    [Fact]
    public void A_duration_is_folded_into_a_typed_literal() =>
        Compile("ends_at - starts_at <= duration('8h')", CelProfile.Rule).Expression!.Root
            .ShouldBeOfType<CelBinary>().Right.ShouldBe(new CelLiteral(CelValueType.Duration, TimeSpan.FromHours(8)));

    [Fact]
    public void The_canonical_writer_spells_a_folded_duration_so_it_compiles_back_to_the_same_tree()
    {
        var root = Compile("ends_at - starts_at <= duration('8h')", CelProfile.Rule).Expression!.Root;
        var text = CelText.Write(root);
        text.ShouldBe("ends_at - starts_at <= duration('28800s')");
        Compile(text, CelProfile.Rule).Expression!.Root.ShouldBe(root);
    }

    [Theory]
    [InlineData("duration('1h')", CelProfile.Computed)]
    [InlineData("starts_at + duration('1h')", CelProfile.Computed)]
    [InlineData("duration('1h') == duration('1h')", CelProfile.Access)]
    public void A_computed_column_and_an_access_level_carry_no_duration(string source, CelProfile profile) =>
        Compile(source, profile).IsSuccess.ShouldBeFalse();

    [Theory]
    [InlineData("duration('1d') > duration('0')", "not a duration")]
    [InlineData("duration('1ns') > duration('0')", "microsecond")]
    public void A_refused_constant_is_an_apply_time_error_naming_why(string source, string reason) =>
        string.Join(" ", Compile(source, CelProfile.Rule).Errors.Select(error => error.Message)).ShouldContain(reason);

    [Fact]
    public void A_duration_result_is_not_a_value_a_mutate_can_store() =>
        Compile("new.ends_at - new.starts_at", CelProfile.Mutate).IsSuccess.ShouldBeFalse();

    [Theory]
    [InlineData(9, true)]
    [InlineData(7, false)]
    public void The_interpreter_subtracts_two_timestamps_into_a_duration(int hours, bool longerThanEight)
    {
        var condition = Compile("new.ends_at - new.starts_at > duration('8h')", CelProfile.Condition).Expression!;
        var row = CelFixtures.Row(("starts_at", _start), ("ends_at", _start.AddHours(hours)));
        CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous).ShouldBe(longerThanEight);
    }

    [Fact]
    public void A_date_counts_as_midnight_utc()
    {
        var condition = Compile("new.due_on + duration('12h') == new.starts_at", CelProfile.Condition).Expression!;
        var row = CelFixtures.Row(("due_on", new DateOnly(2026, 11, 9)), ("starts_at", new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)));
        CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous).ShouldBeTrue();
    }

    [Fact]
    public void A_timestamp_past_year_9999_is_a_range_error_on_the_fail_closed_path()
    {
        var mutate = Compile("starts_at + duration('315576000000s')", CelProfile.Mutate).Expression!;
        var failure = Should.Throw<CelFunctionException>(() =>
            CelInterpreter.EvaluateMutation(mutate, CelFixtures.Row(("starts_at", new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero))), null, _start));
        failure.Message.ShouldContain("outside the range of a Timestamp");
    }

    [Fact]
    public void A_range_error_in_a_rule_s_in_process_evaluation_is_false_like_an_overflow()
    {
        var rule = Compile("starts_at + duration('315576000000s') > ends_at", CelProfile.Rule).Expression!;
        var row = CelFixtures.Row(("starts_at", new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero)), ("ends_at", _start));
        CelInterpreter.EvaluatePredicate(rule, row, null, AlvoContext.Anonymous).ShouldBeFalse();
    }

    /// <summary>
    /// Ruling 57: a range error decides a rule's whole verdict as false, so <c>!</c> cannot turn it into a grant. The
    /// operands are values a caller may write (year 9999), not unreachable ones.
    /// </summary>
    [Theory]
    [InlineData("!(starts_at + duration('24h') > ends_at)")]
    [InlineData("!(starts_at + duration('24h') == ends_at)")]
    [InlineData("!(starts_at + duration('24h') != ends_at)")]
    [InlineData("starts_at + duration('24h') != ends_at")]
    [InlineData("ends_at < starts_at + duration('24h') || !(starts_at + duration('24h') > ends_at)")]
    public void A_range_error_denies_the_whole_rule_even_under_negation(string source)
    {
        var rule = Compile(source, CelProfile.Rule).Expression!;
        var row = CelFixtures.Row(
            ("starts_at", new DateTimeOffset(9999, 12, 31, 12, 0, 0, TimeSpan.Zero)),
            ("ends_at", new DateTimeOffset(9999, 12, 31, 13, 0, 0, TimeSpan.Zero)));
        CelInterpreter.EvaluatePredicate(rule, row, null, AlvoContext.Anonymous).ShouldBeFalse();
    }

    [Fact]
    public void A_range_error_in_a_condition_still_fails_closed_as_the_caller_s()
    {
        var condition = Compile("!(new.starts_at + duration('24h') > new.ends_at)", CelProfile.Condition).Expression!;
        var row = CelFixtures.Row(
            ("starts_at", new DateTimeOffset(9999, 12, 31, 12, 0, 0, TimeSpan.Zero)),
            ("ends_at", new DateTimeOffset(9999, 12, 31, 13, 0, 0, TimeSpan.Zero)));
        var failure = Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous));
        failure.Reason.ShouldBe(CelTemporal.TimestampRange);
        failure.OnCallerData.ShouldBeTrue();
    }

    [Fact]
    public void A_duration_minus_a_timestamp_is_no_overload_the_interpreter_computes()
    {
        Should.Throw<CelFunctionException>(() => CelTemporal.Apply(CelBinaryOperator.Subtract, TimeSpan.FromHours(1), _start, failClosed: true))
            .Reason.ShouldNotBe(CelTemporal.TimestampRange);
        CelTemporal.Apply(CelBinaryOperator.Subtract, TimeSpan.FromHours(1), _start, failClosed: false).ShouldBeNull();
        CelTemporal.Apply(CelBinaryOperator.Add, TimeSpan.FromHours(1), _start, failClosed: false).ShouldBe(_start.AddHours(1));
    }

    /// <summary>
    /// Until temporal comparisons render as integer microseconds, a duration never reaches SQL as a bound
    /// <see cref="TimeSpan"/>: SQLite would compare its text form, so <c>duration('24h') &lt; duration('23h')</c> would hold.
    /// </summary>
    [Theory]
    [InlineData("duration('24h') < duration('23h')")]
    [InlineData("duration('24h') == duration('24h')")]
    public void A_duration_literal_refuses_to_render_rather_than_bind_a_time_span(string source) =>
        Should.Throw<NotSupportedException>(() =>
            new SqlPredicateRenderer().Render(Compile(source, CelProfile.Rule).Expression!, AlvoContext.Anonymous, new TestFieldSqlRenderer()));

    [Theory]
    [InlineData(CelBinaryOperator.Add, 1L)]
    [InlineData(CelBinaryOperator.Subtract, -1L)]
    public void A_duration_sum_past_the_range_is_a_range_error(CelBinaryOperator op, long sign)
    {
        var max = TimeSpan.FromTicks(sign * CelDuration.MaxMagnitude.Ticks);
        var step = TimeSpan.FromTicks(sign * TimeSpan.TicksPerMicrosecond);
        var right = op == CelBinaryOperator.Add ? step : -step;

        Should.Throw<CelFunctionException>(() => CelTemporal.Apply(op, max, right, failClosed: true)).Reason.ShouldBe(CelTemporal.DurationRange);
        Should.Throw<OverflowException>(() => CelTemporal.Apply(op, max, right, failClosed: false));
        CelTemporal.Apply(op, max, TimeSpan.Zero, failClosed: true).ShouldBe(max);
    }

    [Fact]
    public void A_timestamp_before_year_1_is_a_range_error_in_a_condition_and_denies_a_rule()
    {
        var row = CelFixtures.Row(
            ("starts_at", new DateTimeOffset(1, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            ("ends_at", new DateTimeOffset(1, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        var condition = Compile("new.starts_at - duration('24h') < new.ends_at", CelProfile.Condition).Expression!;
        var rule = Compile("starts_at - duration('24h') < ends_at", CelProfile.Rule).Expression!;
        var fits = Compile("new.starts_at - duration('12h') < new.ends_at", CelProfile.Condition).Expression!;

        var failure = Should.Throw<CelFunctionException>(() => CelInterpreter.EvaluatePredicate(condition, row, null, AlvoContext.Anonymous));
        failure.Reason.ShouldBe(CelTemporal.TimestampRange);
        failure.OnCallerData.ShouldBeTrue();
        CelInterpreter.EvaluatePredicate(rule, row, null, AlvoContext.Anonymous).ShouldBeFalse();
        CelInterpreter.EvaluatePredicate(fits, row, null, AlvoContext.Anonymous).ShouldBeTrue();
    }

    [Fact]
    public void A_duration_minus_a_timestamp_is_refused_with_the_way_round_to_write_it()
    {
        var result = Compile("duration('1h') - starts_at < ends_at", CelProfile.Rule);
        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldContain(error =>
            error.Message == "A timestamp cannot be subtracted from a duration; subtract the duration from the timestamp instead.");
    }

    /// <summary>
    /// A temporal subtree of constants alone answers the same on every write, so a result past CEL's range is the author's
    /// error, refused at apply rather than on every write — and never attributed to a caller's data.
    /// </summary>
    [Theory]
    [InlineData("duration('315576000000s') + duration('1us') > duration('0')", CelProfile.Condition, CelTemporal.DurationRange)]
    [InlineData("duration('-315576000000s') - duration('1us') < duration('0')", CelProfile.Condition, CelTemporal.DurationRange)]
    [InlineData("new.starts_at + (duration('315576000000s') + duration('1s')) > new.ends_at", CelProfile.Condition, CelTemporal.DurationRange)]
    [InlineData("starts_at + (duration('315576000000s') + duration('1s'))", CelProfile.Mutate, CelTemporal.DurationRange)]
    [InlineData("duration('315576000000s') + duration('1s') > duration('0')", CelProfile.Rule, CelTemporal.DurationRange)]
    public void Constant_temporal_arithmetic_past_the_range_is_refused_at_compile(string source, CelProfile profile, string reason)
    {
        var result = Compile(source, profile);
        result.IsSuccess.ShouldBeFalse();
        result.Errors.Count(error => error.Message.Contains(reason, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void Constant_temporal_arithmetic_inside_the_range_compiles() =>
        Compile("new.starts_at + (duration('315576000000s') - duration('1s')) > new.ends_at", CelProfile.Condition).IsSuccess.ShouldBeTrue();

    [Fact]
    public void An_empty_operand_makes_the_comparison_false()
    {
        var rule = Compile("ends_at - starts_at <= duration('8h')", CelProfile.Rule).Expression!;
        CelInterpreter.EvaluatePredicate(rule, CelFixtures.Row(("ends_at", _start)), null, AlvoContext.Anonymous).ShouldBeFalse();
    }
}
