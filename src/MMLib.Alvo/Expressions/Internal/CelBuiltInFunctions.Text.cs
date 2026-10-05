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
        var offsets = CodePointOffsets(text);
        var count = offsets.Count - 1;
        var last = end ?? count;
        if (start < 0 || last < start || last > count)
        {
            throw new CelFunctionException("substring", OutsideTheText);
        }

        return text[offsets[(int)start]..offsets[(int)last]];
    }

    /// <summary>The UTF-16 offset where each code point starts, and the text's length last.</summary>
    /// <param name="text">The text to scan.</param>
    /// <returns>One offset per code point, then <c>text.Length</c>.</returns>
    private static List<int> CodePointOffsets(string text)
    {
        var offsets = new List<int>(text.Length + 1);
        for (var at = 0; at < text.Length;)
        {
            offsets.Add(at);
            Rune.DecodeFromUtf16(text.AsSpan(at), out _, out var consumed);
            at += consumed;
        }

        offsets.Add(text.Length);
        return offsets;
    }
}
