using System.Text;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The text built-ins of spec §5.1 beyond C1's: cutting a text, and testing what it holds.</summary>
internal static partial class CelBuiltInFunctions
{
    /// <summary>The one summary both <c>substring</c> overloads share.</summary>
    private const string SubstringSummary =
        "The code points of text from start (counted from 0) up to, not including, end — or to the end of text; a position outside text fails the write.";

    /// <summary>The reason a <c>substring</c> outside its text fails with; it names no position, so it never echoes row data.</summary>
    private const string OutsideTheText = "a position is outside the text";

    private static CelFunction Substring(bool withEnd) => withEnd
        ? InProcess(
            "substring", CelValueType.String, SubstringSummary,
            arguments => SubstringText((string)arguments[0]!, (long)arguments[1]!, (long)arguments[2]!),
            Parameter("text", CelValueType.String), Parameter("start", CelValueType.Int), Parameter("end", CelValueType.Int))
        : InProcess(
            "substring", CelValueType.String, SubstringSummary,
            arguments => SubstringText((string)arguments[0]!, (long)arguments[1]!, end: null),
            Parameter("text", CelValueType.String), Parameter("start", CelValueType.Int));

    /// <summary>Gets <c>contains</c>.</summary>
    /// <remarks>
    /// <c>contains</c>, <c>startsWith</c> and <c>endsWith</c> compare UTF-16 code units ordinally, while <c>size</c> and
    /// <c>substring</c> count code points. So a lone surrogate matches half of a surrogate pair: <c>contains('😀', x)</c>
    /// is true for an <c>x</c> that is the pair's high surrogate alone. <c>CelTextBuiltInTests</c> pins this as today's
    /// behaviour, not a promise.
    /// </remarks>
    private static CelFunction Contains => InProcess(
        "contains", CelValueType.Bool, "Whether text holds search anywhere, comparing characters exactly; an empty search is always held.",
        arguments => ((string)arguments[0]!).Contains((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("search", CelValueType.String));

    private static CelFunction StartsWith => InProcess(
        "startsWith", CelValueType.Bool, "Whether text begins with prefix, comparing characters exactly; every text begins with an empty prefix.",
        arguments => ((string)arguments[0]!).StartsWith((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("prefix", CelValueType.String));

    private static CelFunction EndsWith => InProcess(
        "endsWith", CelValueType.Bool, "Whether text ends with suffix, comparing characters exactly; every text ends with an empty suffix.",
        arguments => ((string)arguments[0]!).EndsWith((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("suffix", CelValueType.String));

    /// <summary>
    /// <c>substring</c>: code points, 0-based, end exclusive (cel-go's semantics). The cut copies the original UTF-16
    /// code units, so a lone surrogate inside the range survives as itself, and counts as one, as <see cref="SizeOf"/> does.
    /// </summary>
    /// <param name="text">The text to cut.</param>
    /// <param name="start">The first code point kept.</param>
    /// <param name="end">The first code point not kept, or <see langword="null"/> for the end of the text.</param>
    /// <returns>The cut text.</returns>
    /// <exception cref="CelFunctionException">A position is negative, reversed or past the end.</exception>
    internal static string SubstringText(string text, long start, long? end)
    {
        var from = start < 0 || end < start ? -1 : CodePointOffset(text, start, fromOffset: 0, fromIndex: 0);
        var to = from >= 0 && end is { } last ? CodePointOffset(text, last, from, start) : text.Length;
        if (from < 0 || to < 0)
        {
            throw new CelFunctionException("substring", OutsideTheText);
        }

        return text[from..to];
    }

    /// <summary>
    /// The UTF-16 offset where code point <paramref name="index"/> starts — <c>text.Length</c> when it is one past the
    /// last — walking on from a known position and stopping there: nothing is allocated, and no text past it is read.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="index">The code point wanted, at least <paramref name="fromIndex"/>.</param>
    /// <param name="fromOffset">A UTF-16 offset already known to start a code point.</param>
    /// <param name="fromIndex">The code point that starts at <paramref name="fromOffset"/>.</param>
    /// <returns>The offset, or <c>-1</c> when the text has fewer code points.</returns>
    private static int CodePointOffset(string text, long index, int fromOffset, long fromIndex)
    {
        var at = fromOffset;
        for (var counted = fromIndex; counted < index; counted++)
        {
            if (at >= text.Length)
            {
                return -1;
            }

            Rune.DecodeFromUtf16(text.AsSpan(at), out _, out var consumed);
            at += consumed;
        }

        return at;
    }
}
