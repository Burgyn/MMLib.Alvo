using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>
/// What Access says about a temporary lockout (docs/todo-admin.md §8d item 39): its end, in the operator's own time
/// once the browser has said what that is, and in UTC, saying so, until then.
/// </summary>
public sealed class LockoutWordsTests
{
    private static readonly DateTimeOffset _until = new(2026, 9, 27, 12, 5, 30, TimeSpan.Zero);

    /// <summary>Five minutes before the lockout ends: the same day in every zone the facts use.</summary>
    private static readonly DateTimeOffset _now = _until.AddMinutes(-5);

    [Fact]
    public void A_locked_person_is_said_to_be_locked_until_the_end_in_the_operators_time()
        => LockoutWords.Of(Person(_until), _now, TimeSpan.FromHours(2))
            .ShouldBe("Locked until 14:05 after failed sign-ins");

    [Fact]
    public void An_offset_west_of_UTC_moves_the_hour_back()
        => LockoutWords.Of(Person(_until), _now, TimeSpan.FromMinutes(-330))
            .ShouldBe("Locked until 06:35 after failed sign-ins");

    /// <summary>Before the browser has said its offset, the time is UTC and says so, never a server's own zone.</summary>
    [Fact]
    public void An_operator_whose_offset_is_not_known_yet_reads_UTC_said_as_such()
        => LockoutWords.Of(Person(_until), _now, offset: null)
            .ShouldBe("Locked until 12:05 UTC after failed sign-ins");

    /// <summary>An offset a browser cannot really have (beyond ±14 h) is ignored, and the time says UTC.</summary>
    [Theory]
    [InlineData(15 * 60)]
    [InlineData(-15 * 60)]
    public void An_offset_outside_what_a_zone_can_be_reads_UTC_said_as_such(int minutes)
        => LockoutWords.Of(Person(_until), _now, TimeSpan.FromMinutes(minutes))
            .ShouldBe("Locked until 12:05 UTC after failed sign-ins");

    /// <summary>
    /// A lockout that ends on another day in the operator's zone says the date: a host may lengthen the lockout, and
    /// "until 00:10" read at 23:55 the day before would otherwise read as twenty-four hours.
    /// </summary>
    [Fact]
    public void A_lockout_ending_on_another_day_in_the_operators_zone_says_the_date()
        => LockoutWords.Of(Person(_until), _until.AddHours(-12), TimeSpan.FromHours(13))
            .ShouldBe("Locked until 2026-09-28 01:05 after failed sign-ins");

    /// <summary>
    /// "Today" is the operator's, not UTC's: 23:50 and 00:30 UTC are two days in UTC and one evening five hours west.
    /// </summary>
    [Fact]
    public void The_same_day_is_decided_in_the_operators_zone()
        => LockoutWords.Of(
                Person(new DateTimeOffset(2026, 9, 28, 0, 30, 0, TimeSpan.Zero)),
                new DateTimeOffset(2026, 9, 27, 23, 50, 0, TimeSpan.Zero),
                TimeSpan.FromHours(-5))
            .ShouldBe("Locked until 19:30 after failed sign-ins");

    [Fact]
    public void A_person_who_is_not_locked_out_gets_no_words()
        => LockoutWords.Of(Person(until: null), _now, TimeSpan.Zero).ShouldBeNull();

    private static AlvoUser Person(DateTimeOffset? until) => new()
    {
        Id = UserId.New(),
        Email = "locked@alvo.test",
        RoleNames = [],
        LockedOutUntil = until,
    };
}
