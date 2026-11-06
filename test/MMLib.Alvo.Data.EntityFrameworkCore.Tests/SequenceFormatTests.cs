using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public sealed class SequenceFormatTests
{
    private static readonly DateTimeOffset _day = new(2026, 11, 6, 23, 30, 0, TimeSpan.Zero);

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

    [Theory]
    [InlineData("north", "north")]
    [InlineData(42L, "42")]
    [InlineData(true, "true")]
    public void A_scope_key_is_the_value_in_invariant_text(object value, string expected) =>
        SequenceFormat.KeyText(value).ShouldBe(expected);

    [Fact]
    public void A_date_and_a_uuid_scope_key_are_their_canonical_text()
    {
        SequenceFormat.KeyText(new DateOnly(2026, 11, 6)).ShouldBe("2026-11-06");
        SequenceFormat.KeyText(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")).ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");
    }
}
