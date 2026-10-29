using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>An instant's floor bounds the ids minted in its millisecond, in the text order the stores compare ids by.</summary>
public sealed class AlvoEventIdFloorTests
{
    private static readonly DateTimeOffset _instant = new(2026, 10, 24, 9, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void The_floor_sorts_after_the_previous_millisecond_and_at_or_before_every_id_of_its_own()
    {
        var floor = AlvoEventId.Floor(_instant);
        var highestOfPrevious = WithTail(AlvoEventId.Floor(_instant.AddMilliseconds(-1)), 0xFF);
        var withinMillisecond = WithTail(floor, 0xFF);
        var nextFloor = AlvoEventId.Floor(_instant.AddMilliseconds(1));

        string.CompareOrdinal(highestOfPrevious.ToString(), floor.ToString()).ShouldBeLessThan(0);
        string.CompareOrdinal(floor.ToString(), withinMillisecond.ToString()).ShouldBeLessThan(0);
        string.CompareOrdinal(withinMillisecond.ToString(), nextFloor.ToString()).ShouldBeLessThan(0);
    }

    [Fact]
    public void The_floor_carries_the_instant_s_milliseconds_in_its_first_six_bytes()
    {
        var bytes = AlvoEventId.Floor(_instant).ToByteArray(bigEndian: true);

        bytes.Take(6).Aggregate(0L, (millis, part) => (millis << 8) | part).ShouldBe(_instant.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void An_instant_before_the_unix_epoch_has_no_floor()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoEventId.Floor(DateTimeOffset.UnixEpoch.AddMilliseconds(-1)));
        AlvoEventId.Floor(DateTimeOffset.UnixEpoch).ToByteArray(bigEndian: true).Take(6).ShouldAllBe(part => part == 0);
    }

    /// <summary>The same id with its last random byte set — an id minted later in the same millisecond.</summary>
    private static Guid WithTail(Guid id, byte last)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        bytes[15] = last;

        return new Guid(bytes, bigEndian: true);
    }
}
