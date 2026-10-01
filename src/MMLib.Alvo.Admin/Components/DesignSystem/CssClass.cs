namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>
/// The class attribute a design-system primitive renders: its own block, its own modifiers, and whatever
/// modifier the page added.
/// </summary>
/// <remarks>
/// <b>Merged, not replaced.</b> A primitive splats the attributes it did not declare onto its element, so a
/// page can name a row by <c>id</c> or <c>data-testid</c> — and a <c>class</c> among them would, splatted
/// alone, overwrite the block class the whole primitive exists to render. The explicit <c>class</c> is
/// therefore written after the splat and carries the page's value with it.
/// </remarks>
internal static class CssClass
{
    /// <summary>The block, the modifiers that are set, and the page's own class, space-separated.</summary>
    /// <param name="block">The class the primitive always renders, such as <c>a-panel</c>.</param>
    /// <param name="attributes">The attributes the primitive captured, which may carry a <c>class</c>.</param>
    /// <param name="modifiers">The primitive's own modifiers; a <see langword="null"/> one is not set.</param>
    /// <returns>The class attribute's value.</returns>
    public static string Of(
        string block, IReadOnlyDictionary<string, object>? attributes, params ReadOnlySpan<string?> modifiers)
    {
        var classes = new List<string> { block };

        foreach (var modifier in modifiers)
        {
            if (modifier is { Length: > 0 })
            {
                classes.Add(modifier);
            }
        }

        if (attributes is not null
            && attributes.TryGetValue("class", out var extra)
            && Convert.ToString(extra, System.Globalization.CultureInfo.InvariantCulture) is { Length: > 0 } text)
        {
            classes.Add(text.Trim());
        }

        return string.Join(' ', classes);
    }
}
