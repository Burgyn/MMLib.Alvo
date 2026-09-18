using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Reads <c>alvo.css</c> as data, so the design system's two promises — both themes declare every
/// colour token, and every foreground/background pair meets WCAG AA — are facts rather than review.
/// </summary>
/// <remarks>
/// It parses <c>light-dark(a, b)</c> rather than two theme blocks on purpose: the stylesheet
/// declares each colour once, so "both themes declare the same names" is structural and what
/// remains to assert is the part a human cannot eyeball, which is the contrast.
/// </remarks>
internal static partial class Stylesheet
{
    /// <summary>The shipped stylesheet — the one the gallery references, never a copy.</summary>
    internal static string AlvoCssPath { get; } = Path.Combine(
        RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "wwwroot", "alvo.css");

    /// <summary>The gallery that proves the stylesheet.</summary>
    internal static string GalleryPath { get; } = Path.Combine(
        RepositoryRoot.Find(), "docs", "design", "gallery.html");

    /// <summary>Which half of a <c>light-dark()</c> pair a caller wants.</summary>
    internal enum Theme
    {
        /// <summary>The first argument.</summary>
        Light = 0,

        /// <summary>The second argument.</summary>
        Dark = 1,
    }

    /// <summary>
    /// Every colour token declared as <c>light-dark(light, dark)</c>, keyed by name including the
    /// leading dashes.
    /// </summary>
    /// <param name="css">The stylesheet's text.</param>
    /// <returns>Token name to its two values, light first.</returns>
    internal static IReadOnlyDictionary<string, (string Light, string Dark)> ReadThemedTokens(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        return ThemedToken()
            .Matches(css)
            .ToDictionary(
                match => match.Groups["name"].Value,
                match => (match.Groups["light"].Value.Trim(), match.Groups["dark"].Value.Trim()),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The WCAG 2.1 contrast ratio between two opaque colours, lighter over darker.
    /// </summary>
    /// <param name="hexA">One colour, as <c>#rrggbb</c>.</param>
    /// <param name="hexB">The other colour, as <c>#rrggbb</c>.</param>
    /// <returns>The ratio, between 1 and 21.</returns>
    internal static double ContrastRatio(string hexA, string hexB)
    {
        var a = RelativeLuminance(hexA);
        var b = RelativeLuminance(hexB);
        var (lighter, darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Every literal colour outside the token block — a hex triple or an <c>rgb</c>/<c>hsl</c>
    /// function in a rule that is not <c>:root</c>.
    /// </summary>
    /// <param name="source">The stylesheet or gallery text to scan.</param>
    /// <returns>The offending literals, in source order.</returns>
    internal static IReadOnlyList<string> LiteralColoursOutsideTokens(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var body = source[TokenBlockEnd(source)..];
        return [.. LiteralColour().Matches(body).Select(match => match.Value)];
    }

    /// <summary>The index just past the <c>:root { … }</c> declaration block.</summary>
    /// <param name="source">The stylesheet text.</param>
    /// <returns>The index, or zero when there is no token block at all.</returns>
    private static int TokenBlockEnd(string source)
    {
        var start = source.IndexOf(":root {", StringComparison.Ordinal);
        if (start < 0)
        {
            return 0;
        }

        var close = source.IndexOf('}', start);
        return close < 0 ? 0 : close + 1;
    }

    /// <summary>The WCAG relative luminance of an <c>#rrggbb</c> colour.</summary>
    /// <param name="hex">The colour.</param>
    /// <returns>Luminance between 0 and 1.</returns>
    private static double RelativeLuminance(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6)
        {
            throw new FormatException($"Expected an #rrggbb colour, got '{hex}'.");
        }

        var red = Linearise(Channel(value, 0));
        var green = Linearise(Channel(value, 2));
        var blue = Linearise(Channel(value, 4));
        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    /// <summary>One channel of an <c>rrggbb</c> string, normalised to 0..1.</summary>
    /// <param name="value">The six hex digits.</param>
    /// <param name="offset">Where the channel starts.</param>
    /// <returns>The channel, normalised.</returns>
    private static double Channel(string value, int offset)
        => byte.Parse(value.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

    /// <summary>Undoes the sRGB transfer function, as WCAG 2.1 defines it.</summary>
    /// <param name="channel">The channel, normalised to 0..1.</param>
    /// <returns>The linear value.</returns>
    private static double Linearise(double channel)
        => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    [GeneratedRegex(
        @"(?<name>--[a-zA-Z0-9-]+)\s*:\s*light-dark\(\s*(?<light>[^,]+?)\s*,\s*(?<dark>.+?)\s*\)\s*;",
        RegexOptions.Compiled)]
    private static partial Regex ThemedToken();

    [GeneratedRegex(
        @"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(",
        RegexOptions.Compiled)]
    private static partial Regex LiteralColour();
}
