namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The operator's own time: which offset from UTC the dashboard may draw a time in (final branch review, item 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Here, not in a feature folder</b>, because <see cref="AdminInterop"/> range-checks the browser's offset where
/// it arrives and every screen that draws a time reads the same answer; a check kept under <c>Components/Access</c>
/// made the circuit's one path into the browser depend on one screen's words.
/// </para>
/// <para>
/// The offset is learned once per circuit by <c>Components.DesignSystem.OperatorClock</c>. Until it is known — and for an offset no
/// zone can have, beyond ±14 hours, which <see cref="DateTimeOffset.ToOffset"/> would throw on in the middle of a
/// render — a time is drawn in UTC and says so, rather than a server's zone passed off as the operator's.
/// </para>
/// </remarks>
internal static class OperatorTime
{
    /// <summary>The furthest a real zone lies from UTC, and the most <see cref="DateTimeOffset.ToOffset"/> accepts.</summary>
    private static readonly TimeSpan _widestZone = TimeSpan.FromHours(14);

    /// <summary>The offset, when it is one a zone can have; otherwise nothing, and a time falls back to UTC.</summary>
    /// <param name="offset">The offset the browser gave, if any.</param>
    /// <returns>A whole-minute offset within ±14 hours, or <see langword="null"/>.</returns>
    public static TimeSpan? Usable(TimeSpan? offset)
        => offset is { } value && value.Duration() <= _widestZone && value.Ticks % TimeSpan.TicksPerMinute == 0
            ? value
            : null;
}
