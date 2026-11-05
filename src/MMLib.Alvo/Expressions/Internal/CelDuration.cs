using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// CEL's <c>duration(string)</c>: Go's <c>time.ParseDuration</c> grammar — an optional sign, then one or more
/// <c>&lt;decimal&gt;&lt;unit&gt;</c> components (<c>h m s ms us µs μs ns</c>), or a bare <c>0</c> — within CEL's range of
/// ±315 576 000 000 seconds, at the microsecond precision Alvo stores instants with (HX-11 Rulings 7, 8, 52).
/// </summary>
internal static class CelDuration
{
    /// <summary>Gets the largest magnitude a duration may have: CEL's ±315 576 000 000 seconds.</summary>
    internal static TimeSpan MaxMagnitude => TimeSpan.FromSeconds(315_576_000_000);

    /// <summary>The fix every refused duration constant carries.</summary>
    internal const string Fix = "Write a CEL duration such as '90m', '1h30m', '-1.5h' or '250ms' (units h, m, s, ms, us, ns).";

    private const string Grammar = "it is not a duration — one or more numbers each followed by a unit, such as 1h30m";
    private const string Precision = "it is not a whole number of microseconds, the precision Alvo stores instants with";
    private const string Range = "it is outside the range of a Duration, about ten thousand years either way";
    private const int MaxNumberLength = 20;
    private const decimal NanosecondsPerTick = 100m;
    private const decimal NanosecondsPerMicrosecond = 1_000m;
    private const decimal MaxNanoseconds = 315_576_000_000m * 1_000_000_000m;

    private static (string Unit, decimal Nanoseconds)[] Units =>
    [
        ("h", 3_600_000_000_000m), ("ms", 1_000_000m), ("m", 60_000_000_000m), ("s", 1_000_000_000m),
        ("us", 1_000m), ("µs", 1_000m), ("μs", 1_000m), ("ns", 1m),
    ];

    /// <summary>Parses a CEL duration constant.</summary>
    /// <param name="text">The constant's text.</param>
    /// <param name="value">The duration, or <see cref="TimeSpan.Zero"/> when refused.</param>
    /// <param name="reason">Why the text is refused, or <see langword="null"/>.</param>
    /// <returns>Whether the text is a duration Alvo admits.</returns>
    internal static bool TryParse(string text, out TimeSpan value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = TimeSpan.Zero;
        var (negative, body) = Signed(text);
        if (body == "0")
        {
            reason = null;
            return true;
        }

        return TrySum(body, out var nanoseconds, out reason) && TryTicks(nanoseconds, negative, out value, out reason);
    }

    /// <summary>The canonical text of a duration: its seconds, e.g. <c>5400s</c> or <c>-0.000001s</c>.</summary>
    /// <param name="value">The duration.</param>
    /// <returns>The text <see cref="TryParse"/> reads back to the same value.</returns>
    internal static string Text(TimeSpan value) =>
        (value.Ticks / (decimal)TimeSpan.TicksPerSecond).ToString("0.######", CultureInfo.InvariantCulture) + "s";

    private static (bool Negative, string Body) Signed(string text) =>
        text.Length > 0 && text[0] is '-' or '+' ? (text[0] == '-', text[1..]) : (false, text);

    private static bool TrySum(string body, out decimal nanoseconds, out string? reason)
    {
        nanoseconds = 0m;
        reason = Grammar;
        var at = 0;
        while (at < body.Length)
        {
            if (!TryComponent(body, ref at, out var component, out reason))
            {
                return false;
            }

            nanoseconds += component;
            if (nanoseconds > MaxNanoseconds)
            {
                reason = Range;
                return false;
            }
        }

        return body.Length > 0;
    }

    private static bool TryComponent(string body, ref int at, out decimal nanoseconds, out string? reason)
    {
        nanoseconds = 0m;
        var number = ReadNumber(body, ref at);
        reason = Grammar;
        if (!IsNumber(number) || !TryUnit(body, ref at, out var perUnit))
        {
            return false;
        }

        var significant = Significant(number);
        reason = LengthRefusal(significant, perUnit);
        if (reason is not null
            || !decimal.TryParse(Padded(significant), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            reason ??= Grammar;
            return false;
        }

        nanoseconds = Multiply(amount, perUnit);
        return true;
    }

    /// <summary>
    /// The number without the zeros that carry no value — leading ones before the point, trailing ones after it — so a
    /// zero-padded constant is judged by its value, never by its spelling's length.
    /// </summary>
    private static string Significant(string number)
    {
        var point = number.IndexOf('.', StringComparison.Ordinal);
        var whole = (point < 0 ? number : number[..point]).TrimStart('0');
        var fraction = point < 0 ? string.Empty : number[(point + 1)..].TrimEnd('0');
        return fraction.Length == 0 ? whole : $"{whole}.{fraction}";
    }

    /// <summary>
    /// Ruling 52's cap on the significant number. A whole part past it is out of range. A number that only its fraction takes
    /// past it is out of range when its whole part already is, and otherwise ends below the microsecond — every in-range
    /// duration that is a whole number of microseconds is spelled in at most 19 significant characters.
    /// </summary>
    private static string? LengthRefusal(string significant, decimal perUnit)
    {
        var wholeLength = WholeLength(significant);
        if (wholeLength > MaxNumberLength)
        {
            return Range;
        }

        if (significant.Length <= MaxNumberLength)
        {
            return null;
        }

        var whole = wholeLength == 0 ? 0m : decimal.Parse(significant[..wholeLength], CultureInfo.InvariantCulture);
        return Multiply(whole, perUnit) > MaxNanoseconds ? Range : Precision;
    }

    private static int WholeLength(string significant)
    {
        var point = significant.IndexOf('.', StringComparison.Ordinal);
        return point < 0 ? significant.Length : point;
    }

    private static string ReadNumber(string body, ref int at)
    {
        var start = at;
        while (at < body.Length && (char.IsAsciiDigit(body[at]) || body[at] == '.'))
        {
            at++;
        }

        return body[start..at];
    }

    private static bool IsNumber(string number) =>
        number.Length > 0 && number.Count(character => character == '.') <= 1 && number != ".";

    private static string Padded(string number) => number.Length == 0 ? "0" : number[0] == '.' ? "0" + number : number;

    private static bool TryUnit(string body, ref int at, out decimal perUnit)
    {
        foreach (var (unit, nanoseconds) in Units)
        {
            if (string.CompareOrdinal(body, at, unit, 0, unit.Length) == 0 && !char.IsAsciiLetterLower(At(body, at + unit.Length)))
            {
                at += unit.Length;
                perUnit = nanoseconds;
                return true;
            }
        }

        perUnit = 0m;
        return false;
    }

    private static char At(string body, int index) => index < body.Length ? body[index] : '\0';

    private static decimal Multiply(decimal amount, decimal perUnit) =>
        amount <= MaxNanoseconds / perUnit ? amount * perUnit : MaxNanoseconds + 1m;

    private static bool TryTicks(decimal nanoseconds, bool negative, out TimeSpan value, out string? reason)
    {
        value = TimeSpan.Zero;
        reason = nanoseconds > MaxNanoseconds ? Range : nanoseconds % NanosecondsPerMicrosecond != 0m ? Precision : null;
        if (reason is not null)
        {
            return false;
        }

        var ticks = (long)(nanoseconds / NanosecondsPerTick);
        value = TimeSpan.FromTicks(negative ? -ticks : ticks);
        return true;
    }
}
