using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>CEL's standard conversions <c>string</c>, <c>int</c> and <c>timestamp</c>, with one text form per type (spec §5.3).</summary>
internal static partial class CelBuiltInFunctions
{
    private const string TimestampFailure = "the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z";

    /// <summary>Gets the two exact forms <see cref="TimestampOf"/> parses once the fraction is cut to seven digits.</summary>
    private static string[] Rfc3339Formats => ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    private static CelFunction String(CelValueType type) => InProcess(
        "string", CelValueType.String,
        "value as text: digits for a number (no trailing zeros), true or false, a lower-case id, or an RFC 3339 instant in UTC.",
        arguments => StringOf(arguments[0]!), Parameter("value", type));

    private static CelFunction Int(CelValueType type) => InProcess(
        "int", CelValueType.Int,
        "value as a whole number: a decimal cut toward zero (2.9 is 2), or a text of digits with an optional sign; anything else fails the write.",
        arguments => type == CelValueType.Decimal ? IntOf((decimal)arguments[0]!) : IntOf((string)arguments[0]!), Parameter("value", type));

    private static CelFunction Timestamp => InProcess(
        "timestamp", CelValueType.Timestamp,
        "text read as an RFC 3339 instant, such as 2026-10-05T12:00:00Z or 2026-10-05T14:00:00+02:00; anything else fails the write.",
        arguments => TimestampOf((string)arguments[0]!), Parameter("text", CelValueType.String));

    /// <summary><c>string</c>: the one text form per type spec §5.3 pins, culture-free.</summary>
    /// <param name="value">An Int (<see cref="long"/>), Decimal, Bool, Uuid or Timestamp.</param>
    /// <returns>Its text.</returns>
    internal static string StringOf(object value) => value switch
    {
        long number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number == 0m ? "0" : number.ToString("0.############################", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("D"),
        DateTimeOffset instant => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z",
        _ => throw new CelFunctionException("string", "the value has no text form"),
    };

    /// <summary><c>int(Decimal)</c>: cut toward zero.</summary>
    /// <param name="value">The decimal.</param>
    /// <returns>The whole number.</returns>
    /// <exception cref="CelFunctionException">The result does not fit an Int.</exception>
    internal static long IntOf(decimal value)
    {
        var truncated = decimal.Truncate(value);
        return truncated is >= long.MinValue and <= long.MaxValue
            ? (long)truncated
            : throw new CelFunctionException("int", "the value is outside the range of an Int");
    }

    /// <summary><c>int(String)</c>: <c>[+-]?[0-9]+</c>, base 10 — Go's <c>ParseInt(s, 10, 64)</c> (deviation F14).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The whole number.</returns>
    /// <exception cref="CelFunctionException">The text is anything else, or out of range.</exception>
    internal static long IntOf(string text)
    {
        var digits = text.AsSpan((text.Length > 0 && text[0] is '+' or '-') ? 1 : 0);
        return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9')
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new CelFunctionException("int", "the text is not a whole number such as 42 or -7");
    }

    /// <summary>
    /// <c>timestamp(String)</c>: RFC 3339 with upper-case <c>T</c> and <c>Z</c>, at most nine fraction digits, of which
    /// the first seven (100 ns, .NET's resolution) are kept (deviation F13).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The instant, with the offset the text gave.</returns>
    /// <exception cref="CelFunctionException">The text is not such a timestamp, or names a day that does not exist.</exception>
    internal static DateTimeOffset TimestampOf(string text)
    {
        var match = Rfc3339().Match(text);
        var fraction = match.Groups["fraction"].Value;
        var kept = $"{match.Groups["main"].Value}{(fraction.Length > 0 ? "." + fraction[..Math.Min(7, fraction.Length)] : string.Empty)}{match.Groups["zone"].Value}";
        return match.Success && DateTimeOffset.TryParseExact(kept, Rfc3339Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : throw new CelFunctionException("timestamp", TimestampFailure);
    }

    /// <summary>RFC 3339 §5.6, ASCII digits only (<c>\d</c> would admit every Unicode digit).</summary>
    [GeneratedRegex(@"^(?<main>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.(?<fraction>[0-9]{1,9}))?(?<zone>Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Rfc3339();
}
