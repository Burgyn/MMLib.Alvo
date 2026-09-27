using System.Globalization;

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

    /// <summary>
    /// <paramref name="instant"/> as the operator reads it: <c>HH:mm</c> in their zone, with the date when it is not
    /// today there, and <c>UTC</c> after it while their offset is not usable.
    /// </summary>
    /// <remarks>
    /// <b>The date only when it is not today</b> in the operator's zone: "until 00:10" read at 23:55 would otherwise
    /// read as a day. One format for every time the dashboard draws, so two times in one editor (a lockout's end and a
    /// credential token's expiry) are read on one clock.
    /// </remarks>
    /// <param name="instant">The time to draw.</param>
    /// <param name="now">The instant "today" is decided at.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <returns>The time, for example <c>14:05</c>, <c>2026-09-28 14:05</c> or <c>14:05 UTC</c>.</returns>
    public static string Clock(DateTimeOffset instant, DateTimeOffset now, TimeSpan? offset)
    {
        var zone = Usable(offset);
        var local = instant.ToOffset(zone ?? TimeSpan.Zero);
        var today = now.ToOffset(zone ?? TimeSpan.Zero).Date == local.Date;
        var clock = local.ToString(today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return zone is null ? $"{clock} UTC" : clock;
    }
}
