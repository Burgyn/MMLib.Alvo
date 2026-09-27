using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>
/// What Access says about a temporary lockout (docs/todo-admin.md §8d item 39): its end, in the operator's own time
/// once the browser has said what that is, and in UTC, saying so, until then.
/// </summary>
public sealed class LockoutWordsTests
{
    private static readonly DateTimeOffset _until = new(2026, 9, 27, 12, 5, 30, TimeSpan.Zero);

    [Fact]
    public void A_locked_person_is_said_to_be_locked_until_the_end_in_the_operators_time()
        => LockoutWords.Of(Person(_until), TimeSpan.FromHours(2))
            .ShouldBe("Locked until 14:05 after failed sign-ins");

    [Fact]
    public void An_offset_west_of_UTC_moves_the_hour_back()
        => LockoutWords.Of(Person(_until), TimeSpan.FromMinutes(-330))
            .ShouldBe("Locked until 06:35 after failed sign-ins");

    /// <summary>Before the browser has said its offset, the time is UTC and says so, never a server's own zone.</summary>
    [Fact]
    public void An_operator_whose_offset_is_not_known_yet_reads_UTC_said_as_such()
        => LockoutWords.Of(Person(_until), offset: null)
            .ShouldBe("Locked until 12:05 UTC after failed sign-ins");

    [Fact]
    public void A_person_who_is_not_locked_out_gets_no_words()
        => LockoutWords.Of(Person(until: null), TimeSpan.Zero).ShouldBeNull();

    private static AlvoUser Person(DateTimeOffset? until) => new()
    {
        Id = UserId.New(),
        Email = "locked@alvo.test",
        RoleNames = [],
        LockedOutUntil = until,
    };
}
