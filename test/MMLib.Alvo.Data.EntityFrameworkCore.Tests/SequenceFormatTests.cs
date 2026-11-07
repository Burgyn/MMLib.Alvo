using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public sealed class SequenceFormatTests
{
    private static readonly DateTimeOffset _day = new(2026, 11, 6, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _newYearInBratislava = new(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SO-{yyyy}-{n:4}", 42L, "SO-2026-0042")]
    [InlineData("{n}", 42L, "42")]
    [InlineData("{n}", 7L, "7")]
    [InlineData("{n:2}", 1234L, "1234")]
    [InlineData("{yy}{MM}{dd}-{n:3}", 5L, "261106-005")]
    [InlineData("{n:18}", 1L, "000000000000000001")]
    public void A_format_renders_its_number_and_the_creates_utc_date(string format, long number, string expected) =>
        SequenceFormat.Render(format, number, _day).ShouldBe(expected);

    [Fact]
    public void The_date_tokens_read_the_utc_date_not_the_offset_one() =>
        SequenceFormat.Render("{yyyy}-{MM}-{dd}", 1, new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(2)))
            .ShouldBe("2026-12-31");

    [Theory]
    [InlineData(SequenceResetPeriod.Year, "2026")]
    [InlineData(SequenceResetPeriod.Month, "2026-11")]
    [InlineData(SequenceResetPeriod.Day, "2026-11-06")]
    [InlineData(null, "")]
    public void A_period_key_names_the_utc_period(SequenceResetPeriod? period, string expected) =>
        SequenceFormat.PeriodKey(period, _day).ShouldBe(expected);

    /// <summary>A <c>timeZone</c> reads the period key and the date tokens from the calendar date in that zone (#479).</summary>
    [Theory]
    [InlineData(SequenceResetPeriod.Year, "2027")]
    [InlineData(SequenceResetPeriod.Month, "2027-01")]
    [InlineData(SequenceResetPeriod.Day, "2027-01-01")]
    public void A_period_key_names_the_period_in_the_declared_zone(SequenceResetPeriod period, string expected) =>
        SequenceFormat.PeriodKey(period, _newYearInBratislava, "Europe/Bratislava").ShouldBe(expected);

    [Fact]
    public void The_date_tokens_read_the_date_in_the_declared_zone() =>
        SequenceFormat.Render("SO-{yyyy}{MM}{dd}-{n}", 1, _newYearInBratislava, "Europe/Bratislava").ShouldBe("SO-20270101-1");

    [Fact]
    public void No_zone_reads_utc()
    {
        SequenceFormat.PeriodKey(SequenceResetPeriod.Day, _newYearInBratislava, timeZone: null).ShouldBe("2026-12-31");
        SequenceFormat.Render("{yyyy}", 1, _newYearInBratislava, timeZone: null).ShouldBe("2026");
    }

    /// <summary>
    /// A DST day keys every instant between its two local midnights — 23 hours on the spring-forward day, 25 on the
    /// fall-back one — and no instant outside them.
    /// </summary>
    [Theory]
    [InlineData("2026-03-28T22:59:59Z", "2026-03-28")]
    [InlineData("2026-03-28T23:00:00Z", "2026-03-29")]
    [InlineData("2026-03-29T21:59:59Z", "2026-03-29")]
    [InlineData("2026-03-29T22:00:00Z", "2026-03-30")]
    [InlineData("2026-10-24T21:59:59Z", "2026-10-24")]
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25")]
    [InlineData("2026-10-25T22:59:59Z", "2026-10-25")]
    [InlineData("2026-10-25T23:00:00Z", "2026-10-26")]
    public void A_dst_day_is_keyed_between_its_local_midnights(string instant, string expected) =>
        SequenceFormat.PeriodKey(
                SequenceResetPeriod.Day,
                DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture),
                "Europe/Bratislava")
            .ShouldBe(expected);

    [Theory]
    [InlineData("Europe/Bratislava", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("UTC", true)]
    [InlineData("Etc/UTC", true)]
    [InlineData("europe/bratislava", false)]
    [InlineData("utc", false)]
    [InlineData("Central Europe Standard Time", false)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    public void A_zone_is_known_when_it_is_an_iana_id_this_machine_resolves(string timeZone, bool known) =>
        SequenceSchema.IsKnownTimeZone(timeZone).ShouldBe(known);

    [Theory]
    [InlineData("north", "north")]
    [InlineData(42L, "42")]
    [InlineData(true, "true")]
    public void A_scope_key_is_the_value_in_invariant_text(object value, string expected) =>
        SequenceFormat.KeyText(value).ShouldBe(expected);

    /// <summary>A missing <c>per</c> value keys the empty count; the insert's own <c>NOT NULL</c> then refuses the row.</summary>
    [Fact]
    public void A_null_scope_key_is_empty_rather_than_a_crash() =>
        SequenceFormat.KeyText(null).ShouldBe(string.Empty);

    [Fact]
    public void A_date_and_a_uuid_scope_key_are_their_canonical_text()
    {
        SequenceFormat.KeyText(new DateOnly(2026, 11, 6)).ShouldBe("2026-11-06");
        SequenceFormat.KeyText(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")).ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");
    }
}
