using MudBlazor;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Alvo's identity as a <see cref="MudTheme"/>: the same colours as alvo.css, the same type, no Material shadows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two themes, one palette each.</b> The theme provider writes one palette per page, server-side, so a dark
/// operator would get light until the circuit connected. <see cref="Light"/> is written on <c>:root</c>, and
/// <see cref="DarkScoped"/> is written under <see cref="DarkScope"/>. alvo.js sets that attribute before the first
/// paint, so the right palette wins with no round trip (study §3.3, verified in the spike).
/// </para>
/// <para>
/// <b>The hex values are alvo.css's, duplicated deliberately</b> (plan deviation V3), and
/// <c>AlvoMudThemeTests</c> pins them equal to the tokens of the same name.
/// </para>
/// </remarks>
internal static class AlvoMudTheme
{
    /// <summary>The attribute selector the dark palette is scoped under; alvo.js always writes it.</summary>
    public const string DarkScope = ":root[data-theme=dark]";

    /// <summary>Every colour the theme carries, by the token it copies.</summary>
    public static IReadOnlyList<ThemeColour> Colours { get; } =
    [
        new("--accent", "#0f7a48", "#39e991"),
        new("--accentText", "#ffffff", "#12241a"),
        new("--bg", "#f7f8fa", "#1e2029"),
        new("--panel", "#ffffff", "#262833"),
        new("--panel2", "#f2f4f7", "#2e3040"),
        new("--border", "#e8eaef", "#34364a"),
        new("--border2", "#d3d7e0", "#3b3e52"),
        new("--text", "#1c1e26", "#e8eaf0"),
        new("--dim", "#5f6577", "#9aa0b8"),
        new("--faint", "#6b7180", "#8b92ab"),
        new("--ok-fg", "#0f7a48", "#39e991"),
        new("--warn-fg", "#8a5a00", "#f5c451"),
        new("--danger-fg", "#b3261e", "#ff8f8f"),
        new("--neutral-fg", "#5f6577", "#9aa0b8"),
    ];

    /// <summary>The theme written on <c>:root</c>; its light palette is the one in force by default.</summary>
    public static MudTheme Light { get; } = Build(scope: null);

    /// <summary>The same theme, written under <see cref="DarkScope"/> by a provider in dark mode.</summary>
    public static MudTheme DarkScoped { get; } = Build(DarkScope);

    private static MudTheme Build(string? scope)
    {
        var theme = new MudTheme
        {
            PaletteLight = Paint(new PaletteLight(), colour => colour.Light),
            PaletteDark = Paint(new PaletteDark(), colour => colour.Dark),
            Typography = Type(),
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
            Shadows = new Shadow { Elevation = [.. Enumerable.Repeat("none", 26)] },
        };

        if (scope is not null)
        {
            theme.PseudoCss = new PseudoCss { Scope = scope };
        }

        return theme;
    }

    private static T Paint<T>(T palette, Func<ThemeColour, string> side) where T : Palette
    {
        string Of(string token) => side(Colours.Single(colour => colour.Token == token));

        palette.Primary = Of("--accent");
        palette.PrimaryContrastText = Of("--accentText");
        palette.Background = Of("--bg");
        palette.BackgroundGray = Of("--panel2");
        palette.Surface = Of("--panel");
        palette.AppbarBackground = Of("--panel");
        palette.AppbarText = Of("--text");
        palette.DrawerBackground = Of("--panel");
        palette.DrawerText = Of("--text");
        palette.TextPrimary = Of("--text");
        palette.TextSecondary = Of("--dim");
        palette.TextDisabled = Of("--faint");
        palette.ActionDefault = Of("--dim");
        palette.LinesDefault = Of("--border");
        palette.LinesInputs = Of("--border2");
        palette.Divider = Of("--border");
        palette.TableLines = Of("--border");
        palette.Success = Of("--ok-fg");
        palette.Warning = Of("--warn-fg");
        palette.Error = Of("--danger-fg");
        palette.ErrorContrastText = Of("--panel");
        palette.Info = Of("--neutral-fg");
        return palette;
    }

    /// <summary>Public Sans at 500 (the fonts folder ships no 400, study §3.2), and no uppercase buttons.</summary>
    private static Typography Type()
    {
        string[] family = ["Public Sans", "system-ui", "-apple-system", "Segoe UI", "sans-serif"];
        return new Typography
        {
            Default = new DefaultTypography { FontFamily = family, FontWeight = "500" },
            Body1 = new Body1Typography { FontFamily = family, FontWeight = "500" },
            Body2 = new Body2Typography { FontFamily = family, FontWeight = "500" },
            Button = new ButtonTypography { FontFamily = family, FontWeight = "600", TextTransform = "none" },
        };
    }
}

/// <summary>One colour the theme carries, and the token it copies.</summary>
/// <param name="Token">The custom property's name in alvo.css, such as <c>--accent</c>.</param>
/// <param name="Light">Its light value, exactly as alvo.css writes it.</param>
/// <param name="Dark">Its dark value, exactly as alvo.css writes it.</param>
internal sealed record ThemeColour(string Token, string Light, string Dark);
