using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>
/// What Access says about a temporary lockout from failed sign-ins (docs/todo-admin.md §8d item 39), on the person's
/// row and in their editor alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the operator's own time</b>, because "locked until 12:05" read in another zone is a wrong answer to "when
/// can they try again?". The dashboard renders on the server, whose zone is nobody's, so the offset is the browser's,
/// learned once per circuit by <see cref="DesignSystem.OperatorClock"/> into <c>AdminInterop.UtcOffset</c>. Until it
/// is known — and for an offset no zone can have, beyond ±14 hours, which <see cref="DateTimeOffset.ToOffset"/> would
/// throw on in the middle of a render — the time is UTC and says so, rather than a server's zone passed off as the
/// operator's.
/// </para>
/// <para>
/// <b>The date only when it is not today</b> in the operator's zone. Identity's default lockout is five minutes and
/// the package does not pin it, so a host that lengthens it can have one end tomorrow; "until 00:10" read at 23:55
/// would then read as a day.
/// </para>
/// </remarks>
internal static class LockoutWords
{
    /// <summary>What the Unlock confirm says first: what ending the lockout does, and what it does not.</summary>
    public const string Consequence =
        "They can try to sign in again now. Nothing else changes: their sessions and credential tokens stay as they "
        + "are, and the next run of wrong passwords locks the account again.";

    /// <summary>The furthest a real zone lies from UTC, and the most <see cref="DateTimeOffset.ToOffset"/> accepts.</summary>
    private static readonly TimeSpan _widestZone = TimeSpan.FromHours(14);

    /// <summary>The sentence for <paramref name="person"/>'s lockout as of now, or <see langword="null"/>.</summary>
    /// <param name="person">The person, as the list read them.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <returns>The sentence, or nothing.</returns>
    public static string? Of(AlvoUser person, TimeSpan? offset) => Of(person, DateTimeOffset.UtcNow, offset);

    /// <summary>The sentence for <paramref name="person"/>'s lockout, or <see langword="null"/> when none stands.</summary>
    /// <param name="person">The person, as the list read them.</param>
    /// <param name="now">The instant "today" is decided at.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <returns>The sentence, or nothing.</returns>
    public static string? Of(AlvoUser person, DateTimeOffset now, TimeSpan? offset)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.LockedOutUntil is not { } until)
        {
            return null;
        }

        var zone = Usable(offset);
        var local = until.ToOffset(zone ?? TimeSpan.Zero);
        var today = now.ToOffset(zone ?? TimeSpan.Zero).Date == local.Date;
        var clock = local.ToString(today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return zone is null
            ? $"Locked until {clock} UTC after failed sign-ins"
            : $"Locked until {clock} after failed sign-ins";
    }

    /// <summary>The offset, when it is one a zone can have; otherwise nothing, and the words fall back to UTC.</summary>
    /// <param name="offset">The offset the browser gave, if any.</param>
    /// <returns>A whole-minute offset within ±14 hours, or <see langword="null"/>.</returns>
    internal static TimeSpan? Usable(TimeSpan? offset)
        => offset is { } value && value.Duration() <= _widestZone && value.Ticks % TimeSpan.TicksPerMinute == 0
            ? value
            : null;
}
