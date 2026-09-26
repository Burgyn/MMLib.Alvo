namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// What "disabled" means over ASP.NET Core Identity's lockout column, in the one place both the store
/// and the administration read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A disable is a lockout with no end; a lockout with an end is not a disable.</b> Identity uses the
/// same column for two things: <c>SetDisabledAsync</c> writes <see cref="DateTimeOffset.MaxValue"/>, and
/// a sign-in with <c>lockoutOnFailure</c> writes <i>now plus a few minutes</i> after repeated wrong
/// passwords. Reading any future lockout as disabled let anyone who knew an operator's address end
/// that operator's open session with five wrong guesses — the bootstrap administrator's included. The
/// temporary lockout still refuses the next sign-in; that is Identity's check and is untouched.
/// </para>
/// <para>
/// <b>Compared against a floor a day below the maximum, not for equality.</b> The value has to survive
/// the store's column: SQLite keeps it as text, exactly, but PostgreSQL's <c>timestamptz</c> keeps
/// microseconds and the maximum has ticks, so an exact comparison would read a disabled account as
/// enabled after a round trip. Nothing but a disable writes a lockout within a day of year 10000.
/// </para>
/// </remarks>
internal static class AlvoIdentityLockout
{
    /// <summary>What <c>SetDisabledAsync</c> writes: Identity's idiom for "indefinitely".</summary>
    internal static DateTimeOffset Disabled => DateTimeOffset.MaxValue;

    private static readonly DateTimeOffset _disabledFloor = DateTimeOffset.MaxValue.AddDays(-1);

    /// <summary>Whether a stored lockout end is a disable rather than a temporary lockout.</summary>
    /// <param name="lockoutEnd">The stored <c>LockoutEnd</c>.</param>
    /// <returns><see langword="true"/> for a disabled account.</returns>
    internal static bool IsDisabled(DateTimeOffset? lockoutEnd) => lockoutEnd >= _disabledFloor;
}
