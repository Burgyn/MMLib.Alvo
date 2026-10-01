using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which offset from UTC the dashboard draws a time in: one a zone can have, or none, and the time says UTC.
/// </summary>
public sealed class OperatorTimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(-330)]
    [InlineData(14 * 60)]
    [InlineData(-14 * 60)]
    public void A_whole_minute_offset_a_zone_can_have_is_usable(int minutes)
        => OperatorTime.Usable(TimeSpan.FromMinutes(minutes)).ShouldBe(TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData(14 * 60 + 1)]
    [InlineData(-15 * 60)]
    public void An_offset_beyond_fourteen_hours_is_not_usable(int minutes)
        => OperatorTime.Usable(TimeSpan.FromMinutes(minutes)).ShouldBeNull();

    [Fact]
    public void An_offset_that_is_not_whole_minutes_is_not_usable()
        => OperatorTime.Usable(TimeSpan.FromSeconds(90)).ShouldBeNull();

    [Fact]
    public void No_offset_is_not_usable()
        => OperatorTime.Usable(null).ShouldBeNull();
}
