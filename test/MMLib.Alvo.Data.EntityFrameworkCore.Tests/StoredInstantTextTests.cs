namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The rendering half of <see cref="StoredInstant"/> — the text <c>VersionRowWriter</c> and
/// <c>IdempotencyTable</c> write into their <c>TEXT</c> columns on both shipped engines. It had no fact of
/// its own, and a rendering that is only ever read back by the same two callers is one that can lose
/// precision without either of them noticing.
/// </summary>
public class StoredInstantTextTests
{
    /// <summary>
    /// A stored instant reads back as the instant that was written, to the tick. A rendering that drops the
    /// fractional second is the failure this guards: an optimistic-concurrency check compares the stamp it
    /// wrote against the stamp it reads, so two writes inside one second would compare equal.
    /// </summary>
    /// <param name="ticks">Ticks past a whole second the instant carries.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(1234567)]
    [InlineData(9999999)]
    public void A_stored_instant_reads_back_as_the_instant_that_was_written(long ticks)
    {
        var instant = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(ticks);

        StoredInstant.Of(StoredInstant.Text(instant)).ShouldBe(instant);
    }

    /// <summary>
    /// Two instants a fraction of a second apart render as two different texts. The round trip above would
    /// still pass if both ends agreed on a coarser rendering; this is what makes the stored value able to
    /// tell two writes apart at all.
    /// </summary>
    [Fact]
    public void Two_instants_inside_one_second_render_as_different_texts()
    {
        var instant = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

        StoredInstant.Text(instant).ShouldNotBe(StoredInstant.Text(instant.AddTicks(10)));
    }

    /// <summary>
    /// The rendering carries the offset, so a value written by a process reading a non-UTC clock is still
    /// read back as the instant it denotes rather than as a wall-clock reading.
    /// </summary>
    [Fact]
    public void A_non_utc_offset_survives_the_round_trip_as_the_instant_it_denotes()
    {
        var spelled = new DateTimeOffset(2026, 7, 26, 14, 0, 0, TimeSpan.FromHours(2));

        StoredInstant.Of(StoredInstant.Text(spelled))
            .ShouldBe(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    }
}
