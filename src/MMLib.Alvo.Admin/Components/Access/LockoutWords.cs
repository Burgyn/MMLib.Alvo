using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>
/// What Access says about a temporary lockout from failed sign-ins (docs/todo-admin.md §8d item 39), on the person's
/// row and in their editor alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the operator's own time</b>, because "locked until 12:05" read in another zone is a wrong answer to "when
/// can they try again?". The dashboard renders on the server, whose zone is nobody's, so the offset is the browser's
/// (<c>OperatorClock</c>); until the circuit has asked it, the time is UTC and says so, rather than a server's zone
/// passed off as the operator's.
/// </para>
/// <para>
/// A lockout of minutes needs no date: it ends today, for anyone reading it while it stands.
/// </para>
/// </remarks>
internal static class LockoutWords
{
    /// <summary>What the Unlock confirm says first: what ending the lockout does, and what it does not.</summary>
    public const string Consequence =
        "They can try to sign in again now. Nothing else changes: their sessions and credential tokens stay as they "
        + "are, and the next run of wrong passwords locks the account again.";

    /// <summary>The sentence for <paramref name="person"/>'s lockout, or <see langword="null"/> when none stands.</summary>
    /// <param name="person">The person, as the list read them.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <returns>The sentence, or nothing.</returns>
    public static string? Of(AlvoUser person, TimeSpan? offset)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.LockedOutUntil is not { } until)
        {
            return null;
        }

        var clock = until.ToOffset(offset ?? TimeSpan.Zero).ToString("HH:mm", CultureInfo.InvariantCulture);
        return offset is null
            ? $"Locked until {clock} UTC after failed sign-ins"
            : $"Locked until {clock} after failed sign-ins";
    }
}
