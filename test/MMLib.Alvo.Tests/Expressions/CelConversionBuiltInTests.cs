using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The conversions of spec §5.3: CEL's standard names, one engine-agnostic text form per type.</summary>
public sealed class CelConversionBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData(-42L, "-42")]
    [InlineData(0L, "0")]
    public void String_of_an_int_is_its_invariant_digits(long value, string expected) =>
        Mutate("string(qty)", ("qty", value)).ShouldBe(expected);

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("2.0", "2")]
    [InlineData("-0.0", "0")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("12345678901234.5678", "12345678901234.5678")]
    public void String_of_a_decimal_is_its_shortest_form(string value, string expected) =>
        Mutate("string(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture))).ShouldBe(expected);

    [Fact]
    public void String_of_a_uuid_is_lower_case_with_hyphens() =>
        Mutate("string(ref_id)", ("ref_id", Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E"))).ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void String_of_a_timestamp_is_rfc_3339_in_utc_without_trailing_zeros()
    {
        CelBuiltInFunctions.StringOf(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))).ShouldBe("2026-10-05T12:00:00Z");
        CelBuiltInFunctions.StringOf(new DateTimeOffset(2026, 10, 5, 12, 0, 0, 500, TimeSpan.Zero)).ShouldBe("2026-10-05T12:00:00.5Z");
    }

    /// <summary>
    /// A <c>date</c> reaches CEL as midnight UTC, so its text would pin <c>…T00:00:00Z</c> — a form a later Date type would
    /// want to change, which a stored value forbids. Refused until then (spec E18, D-9); a <c>datetime</c> is fine.
    /// </summary>
    [Theory]
    [InlineData("string(due)", CelProfile.Mutate)]
    [InlineData("string(new.due)", CelProfile.Mutate)]
    [InlineData("string(old.due) == 'x'", CelProfile.Condition)]
    public void String_of_a_date_field_is_refused_at_apply(string source, CelProfile profile)
    {
        var error = TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "'string(...)' cannot take the date field 'due' yet: its text form is not settled, and a hook that stored one could not change it later.");
        error.FixSuggestion.ShouldBe("Store the date's text from the client, or make 'due' a datetime field, whose text is an RFC 3339 instant.");
        error.Position.ShouldBe(source.IndexOf("string", StringComparison.Ordinal));
    }

    [Fact]
    public void String_of_a_datetime_field_compiles()
    {
        var withMoment = TestCelFunctions.Items with
        {
            Fields = [.. TestCelFunctions.Items.Fields, new FieldSchema { Name = "moment", Type = FieldType.DateTime, Nullable = true }],
        };

        TestCelFunctions.Compiler().Compile("string(moment)", CelProfile.Mutate, withMoment).IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    /// The cel-spec subset Alvo leaves out on purpose (spec §16 F12–F14): <c>string(string)</c>, <c>timestamp(timestamp)</c>,
    /// <c>timestamp(int)</c> and <c>int(timestamp)</c> have no overload here, so a call to one is refused at apply — never
    /// coerced, never a run-time surprise.
    /// </summary>
    [Theory]
    [InlineData("string(name)", "string", "String")]
    [InlineData("timestamp(now())", "timestamp", "Timestamp")]
    [InlineData("timestamp(qty)", "timestamp", "Int")]
    [InlineData("int(now())", "int", "Timestamp")]
    public void A_left_out_conversion_overload_is_refused_at_apply(string source, string function, string argumentType) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldStartWith($"'{function}(...)' accepts no ({argumentType}); it accepts {function}(");

    /// <summary>
    /// <c>int(int)</c> is the one cel-spec identity with no overload of its own that still compiles: an Int argument binds
    /// <c>int(value: Decimal)</c> (F7 widening), and cutting a whole number toward zero is the number itself — every
    /// 64-bit value fits a decimal exactly.
    /// </summary>
    [Fact]
    public void Int_of_an_int_binds_the_decimal_overload_and_is_the_identity()
    {
        var call = TestCelFunctions.Compile("int(qty)", CelProfile.Mutate).Root.ShouldBeOfType<CelCall>();

        call.Function.ShouldNotBeNull().Parameters.ShouldHaveSingleItem().Type.ShouldBe(CelValueType.Decimal);
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile("int(qty)", CelProfile.Mutate), CelFixtures.Row(("qty", 42L)), previous: null, _now)
            .ShouldBe(42L);
        CelBuiltInFunctions.IntOf((decimal)long.MinValue).ShouldBe(long.MinValue);
        CelBuiltInFunctions.IntOf((decimal)long.MaxValue).ShouldBe(long.MaxValue);
    }

    [Fact]
    public void String_of_a_bool_is_true_or_false()
    {
        CelBuiltInFunctions.StringOf(true).ShouldBe("true");
        CelBuiltInFunctions.StringOf(false).ShouldBe("false");
    }

    [Theory]
    [InlineData("2.9", 2L)]
    [InlineData("-2.9", -2L)]
    public void Int_of_a_decimal_truncates_toward_zero(string value, long expected) =>
        Mutate("int(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture))).ShouldBe(expected);

    [Theory]
    [InlineData("+7", 7L)]
    [InlineData("-7", -7L)]
    [InlineData("007", 7L)]
    public void Int_of_a_text_reads_base_ten_with_a_sign(string text, long expected) =>
        Mutate("int(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData(" 7")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    [InlineData("٣")]
    public void Int_of_any_other_text_fails_closed(string text) =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.IntOf(text)).Reason.ShouldBe("the text is not a whole number such as 42 or -7");

    [Fact]
    public void Int_of_a_decimal_past_the_int_range_fails_closed() =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.IntOf(1e28m)).Reason.ShouldBe("the value is outside the range of an Int");

    [Theory]
    [InlineData("2026-10-05T12:00:00Z")]
    [InlineData("2026-10-05T14:00:00+02:00")]
    [InlineData("2026-10-05T12:00:00.000000000Z")]
    public void Timestamp_reads_rfc_3339(string text) =>
        CelBuiltInFunctions.TimestampOf(text).ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Timestamp_keeps_a_hundred_nanoseconds_and_drops_the_rest() =>
        CelBuiltInFunctions.TimestampOf("2026-10-05T12:00:00.123456789Z").Ticks.ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).Ticks + 1_234_567);

    [Theory]
    [InlineData("2026-10-05")]
    [InlineData("2026-10-05 12:00:00Z")]
    [InlineData("2026-10-05t12:00:00z")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-10-05T12:00:00")]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("2026-10-05T12:00:00.1234567890Z")]
    [InlineData("２０２６-10-05T12:00:00Z")]
    public void Timestamp_of_anything_else_fails_closed(string text) =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.TimestampOf(text)).Reason
            .ShouldBe("the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z");

    [Fact]
    public void A_date_field_compares_with_a_fixed_instant_in_a_condition() =>
        CelInterpreter.EvaluatePredicate(
            TestCelFunctions.Compile("new.due < timestamp('2026-12-01T00:00:00Z')", CelProfile.Condition),
            CelFixtures.Row(("due", new DateOnly(2026, 10, 5))), previous: null, AlvoContext.Anonymous).ShouldBeTrue();
}
