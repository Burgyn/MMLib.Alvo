namespace MMLib.Alvo.Admin.Components.Schema.Map;

/// <summary>
/// What a box's row can hold: a field's name on the left and its detail right-aligned, in the mono face,
/// cut to the box's width so the two never run into each other.
/// </summary>
/// <remarks>
/// <b>A character budget, not a measurement.</b> The server draws the picture before any browser exists to
/// measure it, and a monospaced face makes a character count a width — 11 px IBM Plex Mono is about 6.6 px a
/// character. The detail is cut first, because the name is what the row is; the row's tooltip carries both whole.
/// </remarks>
internal static class MapText
{
    /// <summary>The width of one character of a row, 11 px mono, in layout units.</summary>
    public const double CharWidth = 6.6;

    /// <summary>A row's inset from the box's edge, on either side.</summary>
    public const double Inset = 12;

    /// <summary>The characters kept between a name and its detail.</summary>
    public const int Gap = 2;

    /// <summary>The shortest a detail is cut to before the name is cut instead.</summary>
    public const int MinDetail = 8;

    /// <summary>How many characters one row of a box holds, name, gap and detail together.</summary>
    public static int RowChars => (int)((MapLayout.BoxWidth - 2 * Inset) / CharWidth);

    /// <summary>A row's name and detail, each cut with an ellipsis until both fit <paramref name="budget"/>.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="detail">The field's detail — its type, its target, its rollup.</param>
    /// <param name="budget">The characters the row holds, gap included.</param>
    public static (string Name, string Detail) Fit(string name, string detail, int budget)
    {
        var room = budget - Gap;
        if (name.Length + detail.Length <= room)
        {
            return (name, detail);
        }

        var cutDetail = Clip(detail, Math.Max(room - name.Length, Math.Min(detail.Length, MinDetail)));
        return (Clip(name, room - cutDetail.Length), cutDetail);
    }

    /// <summary><paramref name="text"/> cut to <paramref name="length"/> characters, the last an ellipsis.</summary>
    private static string Clip(string text, int length)
        => text.Length <= length ? text : length <= 1 ? "…" : string.Concat(text.AsSpan(0, length - 1), "…");
}
