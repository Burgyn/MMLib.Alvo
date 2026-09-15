using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The one funnel every caller-supplied value crosses on its way to a column, tested where it lives rather
/// than only through the two paths that call it.
/// </summary>
/// <remarks>
/// Its three refusals — a NUL inside text, a fractional value against an integral column, a value the
/// column's type cannot hold — are the ones a caller reaches by writing a request, so each is asserted here
/// against the funnel itself. The same facts are reachable through the SQLite binder, but a rule with one
/// implementation and no direct test is a rule whose arms rot individually while both callers happen to
/// exercise the same one; and every fact below needs no database to be true.
/// </remarks>
public class ColumnValueTests
{
    private const string Column = "mileage";

    /// <summary>
    /// The argument guards fire before the <see langword="null"/> short-circuit: a caller that hands this
    /// funnel no column type has a bug, and returning <see langword="null"/> for it would hide the bug until
    /// the next value that is not null.
    /// </summary>
    [Fact]
    public void A_null_column_type_is_refused_even_when_the_value_is_null()
        => Should.Throw<ArgumentNullException>(() => ColumnValue.For(null!, "note", null));

    [Fact]
    public void A_null_column_name_is_refused_even_when_the_value_is_null()
        => Should.Throw<ArgumentNullException>(() => ColumnValue.For(typeof(string), null!, null));

    /// <summary>A nameless column cannot appear in a refusal message, so it is refused first.</summary>
    /// <param name="column">The blank name a caller supplied.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void A_blank_column_name_is_refused_even_when_the_value_is_null(string column)
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(string), column, null));

    [Fact]
    public void A_null_value_is_left_alone()
        => ColumnValue.For(typeof(int?), Column, null).ShouldBeNull();

    /// <summary>
    /// A NUL inside text is refused for a <c>text</c> column — the one value no engine Alvo supports can
    /// carry, and the case where the short-circuit for "already the column's type" would otherwise wave it
    /// through to PostgreSQL as an unhandled <c>22021</c> and to SQLite as a silent answer.
    /// </summary>
    /// <param name="text">Where the caller put the NUL.</param>
    [Theory]
    [InlineData("\0")]
    [InlineData("a\0b")]
    [InlineData("ab\0")]
    public void Text_containing_a_nul_is_refused_by_the_column_it_already_has_the_type_of(string text)
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(string), "note", text));

    [Fact]
    public void Text_without_a_nul_reaches_the_column_unchanged()
        => ColumnValue.For(typeof(string), "note", "ACME-001").ShouldBe("ACME-001");

    /// <summary>
    /// The NUL refusal names both the character to remove and the column it arrived at — the only two things
    /// that let a caller fix the request, since every refusal here shares one exception type and one
    /// parameter name.
    /// </summary>
    [Fact]
    public void The_nul_refusal_names_the_character_to_remove_and_the_column()
    {
        var refusal = Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(string), "note", "a\0b"));

        refusal.Message.ShouldContain("NUL");
        refusal.Message.ShouldContain("note");
        refusal.ParamName.ShouldBe("value");
    }

    /// <summary>
    /// A value the column's type does not already hold is converted rather than handed on as it arrived —
    /// the JSON string an agent emits for an <c>integer</c> field being the case that reaches this most.
    /// </summary>
    [Fact]
    public void A_value_the_column_type_does_not_hold_is_converted_rather_than_passed_through()
    {
        var converted = ColumnValue.For(typeof(int), Column, "42");

        converted.ShouldBeOfType<int>();
        converted.ShouldBe(42);
    }

    [Fact]
    public void Text_becomes_the_guid_a_uuid_column_holds()
        => ColumnValue.For(typeof(Guid), "owner_id", "6f9619ff-8b86-d011-b42d-00cf4fc964ff")
            .ShouldBe(new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff"));

    [Fact]
    public void Text_becomes_the_calendar_date_a_date_column_holds()
        => ColumnValue.For(typeof(DateOnly), "due_on", "2026-07-26").ShouldBe(new DateOnly(2026, 7, 26));

    /// <summary>
    /// A <c>date</c> column takes the calendar date the caller wrote in the offset they wrote it with, not
    /// the UTC date — which would shift the day for every caller east or west of UTC.
    /// </summary>
    [Fact]
    public void A_timestamp_becomes_the_calendar_date_the_caller_wrote()
        => ColumnValue.For(typeof(DateOnly), "due_on", new DateTimeOffset(2026, 7, 26, 1, 0, 0, TimeSpan.FromHours(2)))
            .ShouldBe(new DateOnly(2026, 7, 26));

    [Fact]
    public void Text_becomes_the_time_of_day_a_time_column_holds()
        => ColumnValue.For(typeof(TimeOnly), "opens_at", "08:30").ShouldBe(new TimeOnly(8, 30));

    /// <summary>
    /// Text a <c>uuid</c> column cannot parse leaves the funnel as the caller-supplied-value refusal every
    /// other rejection here uses, not as the raw <see cref="FormatException"/> the parser threw — that is
    /// the port's malformed-query channel, and it is what makes the difference between a 422 and a 500.
    /// </summary>
    [Fact]
    public void Text_a_uuid_column_cannot_parse_is_refused_as_a_bad_argument()
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(Guid), "owner_id", "not-a-uuid"))
            .InnerException.ShouldBeOfType<FormatException>();

    /// <summary>A value that cannot even be read as text for parsing takes the same channel.</summary>
    [Fact]
    public void A_value_a_uuid_column_cannot_read_as_text_is_refused_as_a_bad_argument()
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(Guid), "owner_id", 42))
            .InnerException.ShouldBeOfType<InvalidCastException>();

    /// <summary>And so does a value of the right shape that the column's type is too narrow to hold.</summary>
    [Fact]
    public void A_value_too_large_for_the_column_is_refused_as_a_bad_argument()
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(byte), "flag", 300))
            .InnerException.ShouldBeOfType<OverflowException>();

    /// <summary>
    /// The refusal names the column, the type the value arrived as and the type the column holds — the three
    /// facts an agent needs to correct the request without a second round trip.
    /// </summary>
    [Fact]
    public void The_conversion_refusal_names_the_column_the_value_type_and_the_column_type()
    {
        var refusal = Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(Guid), "owner_id", 42));

        refusal.Message.ShouldContain("owner_id");
        refusal.Message.ShouldContain(typeof(int).ToString());
        refusal.Message.ShouldContain(typeof(Guid).ToString());
        refusal.ParamName.ShouldBe("value");
    }

    /// <summary>
    /// A fractional value against an integral column is refused, not rounded.
    /// <see cref="System.Convert.ChangeType(object?, Type, IFormatProvider?)"/> rounds midpoint-to-even, so
    /// <c>mileage=gt.12.7</c> would have answered <c>mileage &gt; 13</c> — dropping the row the caller's own
    /// predicate included, silently.
    /// </summary>
    /// <param name="fraction">The fractional bound a caller wrote.</param>
    [Theory]
    [InlineData(12.7)]
    [InlineData(12.5)]
    [InlineData(-0.5)]
    [InlineData(13.5)]
    public void A_fractional_value_against_an_integral_column_is_refused_rather_than_rounded(double fraction)
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(long), Column, (decimal)fraction));

    /// <summary>Every integral column type the map can produce refuses it, not only the common one.</summary>
    /// <param name="clrType">The integral column type.</param>
    [Theory]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(short))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(int))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(long))]
    [InlineData(typeof(ulong))]
    public void Every_integral_column_type_refuses_a_fractional_value(Type clrType)
        => Should.Throw<ArgumentException>(() => ColumnValue.For(clrType, Column, 12.5m));

    /// <summary>
    /// The refusal names the value that would have been rounded. Both this and an unreadable value reach the
    /// caller as one <see cref="ArgumentException"/> naming the column and the two types, so the inner
    /// message is the only thing that tells "drop the decimal point" apart from "wrong type entirely".
    /// </summary>
    [Fact]
    public void The_fraction_refusal_names_the_value_that_would_have_been_rounded()
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(long), Column, 12.7m))
            .InnerException.ShouldBeOfType<InvalidCastException>()
            .Message.ShouldContain(12.7m.ToString(CultureInfo.CurrentCulture));

    /// <summary>
    /// The refusal is about losing information, not about the arriving type: a whole number fits an integral
    /// column whichever numeric type — or text — carried it.
    /// </summary>
    /// <param name="value">The whole value, as one of the types a caller's JSON can produce.</param>
    [Theory]
    [MemberData(nameof(WholeValues))]
    public void A_whole_number_fits_an_integral_column_whatever_type_carried_it(object value)
        => ColumnValue.For(typeof(long), Column, value).ShouldBe(13L);

    /// <summary>The mirror: the fraction is refused whichever floating-point type carried it.</summary>
    /// <param name="value">The fractional value, as one of the types a caller's JSON can produce.</param>
    [Theory]
    [MemberData(nameof(FractionalValues))]
    public void A_fraction_is_refused_whatever_numeric_type_carried_it(object value)
        => Should.Throw<ArgumentException>(() => ColumnValue.For(typeof(long), Column, value));

    /// <summary>
    /// And it is about the column, not the value: a fractional value against a column that holds fractions is
    /// exactly what that column is for.
    /// </summary>
    [Fact]
    public void A_fractional_value_against_a_decimal_column_is_untouched()
        => ColumnValue.For(typeof(decimal), "price", 12.7).ShouldBe(12.7m);

    [Fact]
    public void A_fractional_value_against_a_double_column_is_untouched()
        => ColumnValue.For(typeof(double), "ratio", 12.5m).ShouldBe(12.5d);

    /// <summary>The whole values, one per type a JSON number or an agent's string arrives as.</summary>
    public static TheoryData<object> WholeValues() => new() { 13m, 13d, 13f, 13, 13L, "13" };

    /// <summary>The fractional ones, one per floating-point type the funnel inspects.</summary>
    public static TheoryData<object> FractionalValues() => new() { 12.5m, 12.5d, 12.5f };
}
