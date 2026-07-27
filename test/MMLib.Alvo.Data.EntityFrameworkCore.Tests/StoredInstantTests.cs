namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The one authority for what instant a timestamp denotes, tested where it lives rather than only through the
/// two paths that call it — a rule with one implementation and no direct test is a rule whose arms can rot
/// individually while both callers happen to exercise the same one.
/// </summary>
public class StoredInstantTests
{
    private static readonly DateTimeOffset _noon = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Whatever offset a caller spelled, the bound value is the instant it denotes.</summary>
    /// <param name="hours">The offset the same instant is expressed at.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-5)]
    [InlineData(14)]
    public void An_offset_is_a_spelling_of_an_instant_not_a_part_of_it(int hours)
    {
        var spelled = _noon.ToOffset(TimeSpan.FromHours(hours));

        var stored = StoredInstant.Of(spelled);

        stored.ShouldBe(_noon);
        stored.Offset.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    /// A kindless <see cref="DateTime"/> — what <c>System.Text.Json</c> produces for an offset-less JSON
    /// timestamp — is read <em>as</em> UTC, not in the host's zone.
    /// </summary>
    [Fact]
    public void A_kindless_datetime_is_read_as_utc()
        => StoredInstant.Of(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Unspecified)).ShouldBe(_noon);

    [Fact]
    public void A_utc_datetime_keeps_its_instant()
        => StoredInstant.Of(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)).ShouldBe(_noon);

    /// <summary>
    /// A <see cref="DateTimeKind.Local"/> value denotes the right instant but carries the host's offset, so the
    /// result must be normalised rather than merely wrapped. Asserting the offset as well as the instant is
    /// what makes this fact fail if the <c>ToUniversalTime()</c> is dropped — on a UTC host the instant alone
    /// would agree either way.
    /// </summary>
    [Fact]
    public void A_local_datetime_is_normalised_rather_than_merely_wrapped()
    {
        var local = _noon.ToLocalTime().LocalDateTime;

        var stored = StoredInstant.Of(local);

        stored.ShouldBe(_noon);
        stored.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_date_becomes_midnight_utc()
        => StoredInstant.Of(new DateOnly(2026, 7, 26))
            .ShouldBe(new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Text is parsed as UTC when it carries no offset, and normalised when it does.</summary>
    /// <param name="text">The caller's spelling.</param>
    [Theory]
    [InlineData("2026-07-26T12:00:00")]
    [InlineData("2026-07-26T12:00:00Z")]
    [InlineData("2026-07-26T14:00:00+02:00")]
    [InlineData("2026-07-26T07:00:00-05:00")]
    public void Text_is_read_as_one_instant_however_it_is_spelled(string text)
        => StoredInstant.Of(text).ShouldBe(_noon);

    [Fact]
    public void Text_that_is_not_a_timestamp_is_refused()
        => Should.Throw<FormatException>(() => StoredInstant.Of("not-a-timestamp"));

    [Fact]
    public void A_value_that_is_not_a_timestamp_at_all_is_refused()
        => Should.Throw<InvalidCastException>(() => StoredInstant.Of(42));

    /// <summary>
    /// The column funnel every path now shares. A timestamp column normalises; every other column keeps its
    /// own rule, so a <c>date</c> keeps the calendar-date rule that is deliberately <em>not</em> this class's.
    /// </summary>
    /// <remarks>
    /// Asserted through <c>ColumnValue</c> rather than through a write-path-only gate of its own, because
    /// having two entry points is how the write path came to apply this normalisation and none of the funnel's
    /// other rules.
    /// </remarks>
    [Fact]
    public void Only_a_timestamp_column_normalises_its_value()
    {
        var spelled = _noon.ToOffset(TimeSpan.FromHours(-5));

        ColumnValue.For(typeof(DateTimeOffset?), "occurred_at", spelled).ShouldBe(_noon);
        ColumnValue.For(typeof(DateTimeOffset), "occurred_at", spelled).ShouldBe(_noon);
        ColumnValue.For(typeof(DateOnly?), "due_on", new DateOnly(2026, 7, 26)).ShouldBe(new DateOnly(2026, 7, 26));
        ColumnValue.For(typeof(string), "note", "2026-07-26T12:00:00").ShouldBe("2026-07-26T12:00:00");
    }

    /// <summary>
    /// Text a timestamp column can read is now <b>converted</b> rather than passed through for EF to reject:
    /// the read path already accepted it, and two answers to one question is the defect
    /// <c>ColumnValue</c> exists to remove.
    /// </summary>
    [Fact]
    public void Timestamp_text_for_a_timestamp_column_is_converted_rather_than_left_to_ef()
        => ColumnValue.For(typeof(DateTimeOffset?), "occurred_at", "2026-07-26T12:00:00Z").ShouldBe(_noon);

    [Fact]
    public void A_null_is_left_alone()
        => ColumnValue.For(typeof(DateTimeOffset?), "occurred_at", null).ShouldBeNull();

    /// <summary>
    /// The gate's own predicate, over every column type this read model can produce — a <c>date</c> and a
    /// <c>DateTime</c> are the two that look like instants and are not.
    /// </summary>
    /// <param name="clrType">The candidate column type.</param>
    /// <param name="expected">Whether it holds an instant.</param>
    [Theory]
    [InlineData(typeof(DateTimeOffset), true)]
    [InlineData(typeof(DateTimeOffset?), true)]
    [InlineData(typeof(DateOnly), false)]
    [InlineData(typeof(DateOnly?), false)]
    [InlineData(typeof(DateTime), false)]
    [InlineData(typeof(string), false)]
    public void A_column_holds_an_instant_only_when_it_is_a_datetimeoffset(Type clrType, bool expected)
        => StoredInstant.IsTimestamp(clrType).ShouldBe(expected);
}
