using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Schema;

/// <summary>
/// Renders a sequence number into its declared <see cref="SequenceSchema.Format"/>, and names the calendar period a create
/// counts in — one implementation for every store that assigns a number, so a formatted code reads the same on each.
/// </summary>
internal static class SequenceFormat
{
    /// <summary>The text <paramref name="format"/> stores for <paramref name="number"/> on a create at <paramref name="instant"/>.</summary>
    /// <param name="format">A format <see cref="SequenceSchema.IsValidFormat"/> accepts.</param>
    /// <param name="number">The allocated number.</param>
    /// <param name="instant">The create's stamp; its UTC date fills the date tokens.</param>
    internal static string Render(string format, long number, DateTimeOffset instant) =>
        SequenceSchema.FormatToken().Replace(format, match => Part(match, number, instant.UtcDateTime));

    /// <summary>The key of the period a create at <paramref name="instant"/> counts in; empty when the count never restarts.</summary>
    /// <param name="period">The declared period, or <see langword="null"/>.</param>
    /// <param name="instant">The create's stamp, read in UTC.</param>
    internal static string PeriodKey(SequenceResetPeriod? period, DateTimeOffset instant) => period switch
    {
        SequenceResetPeriod.Year => Text(instant.UtcDateTime, "yyyy"),
        SequenceResetPeriod.Month => Text(instant.UtcDateTime, "yyyy-MM"),
        SequenceResetPeriod.Day => Text(instant.UtcDateTime, "yyyy-MM-dd"),
        _ => string.Empty,
    };

    /// <summary>
    /// A key value in invariant text — a <c>per</c> value or a tenant id — so every store and every culture names one count
    /// the same way: a date as <c>yyyy-MM-dd</c>, a boolean as <c>true</c>/<c>false</c>, a uuid in its canonical form.
    /// </summary>
    /// <param name="value">The row's value; <see langword="null"/> keys the empty count, and the insert's own
    /// <c>NOT NULL</c> on the required <c>per</c> field then refuses the row and returns the number.</param>
    internal static string KeyText(object? value) => value switch
    {
        null => string.Empty,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Part(Match match, long number, DateTime utc) => match.Groups["token"].Value switch
    {
        "yyyy" or "yy" or "MM" or "dd" => Text(utc, match.Groups["token"].Value),
        _ when match.Groups["width"].Success => number.ToString("D" + match.Groups["width"].Value, CultureInfo.InvariantCulture),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    private static string Text(DateTime utc, string pattern) => utc.ToString(pattern, CultureInfo.InvariantCulture);
}
