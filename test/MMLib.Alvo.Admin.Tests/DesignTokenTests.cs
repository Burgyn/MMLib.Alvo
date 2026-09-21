using MMLib.Alvo.Admin.Tests.Internal;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// The design system's two promises, asserted rather than reviewed: every colour token carries
/// both themes, and every foreground/background pair meets WCAG AA in each of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why contrast is a test and not a checklist item.</b> The analysis lists WCAG AA among §2.8's
/// "must contain", and the design's deviation D6 exists because the reference drawing's own accent
/// failed it by a margin no eye catches — 4.39:1 against a 4.5:1 bar. A rule that is checked once
/// at audit time is a rule that regresses on the next colour tweak.
/// </para>
/// </remarks>
public sealed class DesignTokenTests
{
    private static readonly string _css = File.ReadAllText(Stylesheet.AlvoCssPath);

    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> _tokens =
        Stylesheet.ReadThemedTokens(_css);

    /// <summary>
    /// Every pair the UI actually renders text over. Surfaces first, then the accent, which is the
    /// one D6 moved.
    /// </summary>
    public static TheoryData<string, string> ForegroundPairs() => new()
    {
        { "--accent", "--accentText" },
        { "--bg", "--text" },
        { "--bg", "--dim" },
        { "--bg", "--faint" },
        { "--panel", "--text" },
        { "--panel", "--dim" },
        { "--panel", "--faint" },
        { "--panel2", "--text" },
        { "--panel", "--ok-fg" },
        { "--panel", "--warn-fg" },
        { "--panel", "--danger-fg" },
        { "--panel", "--neutral-fg" },
    };

    [Fact]
    public void The_stylesheet_declares_the_colour_tokens_the_system_is_built_from()
    {
        string[] required =
        [
            "--bg", "--panel", "--panel2", "--codeBg", "--border", "--border2",
            "--text", "--dim", "--faint",
            "--accent", "--accentText", "--accentSoft", "--accentBorder",
            "--ok-fg", "--ok-bg", "--warn-fg", "--warn-bg",
            "--danger-fg", "--danger-bg", "--neutral-fg", "--neutral-bg",
        ];

        foreach (var token in required)
        {
            _tokens.Keys.ShouldContain(token);
        }
    }

    [Theory]
    [MemberData(nameof(ForegroundPairs))]
    public void Every_foreground_pair_meets_WCAG_AA_in_both_themes(string background, string foreground)
    {
        var light = Stylesheet.ContrastRatio(_tokens[background].Light, _tokens[foreground].Light);
        var dark = Stylesheet.ContrastRatio(_tokens[background].Dark, _tokens[foreground].Dark);

        light.ShouldBeGreaterThanOrEqualTo(
            4.5, $"light: {foreground} on {background} measures {light:0.00}:1");
        dark.ShouldBeGreaterThanOrEqualTo(
            4.5, $"dark: {foreground} on {background} measures {dark:0.00}:1");
    }

    /// <summary>
    /// Pins deviation D6 specifically, beside the theory that would also catch it.
    /// </summary>
    /// <remarks>
    /// The theory proves the accent passes; this proves it is <em>this</em> accent. Swapping it for
    /// another passing colour should be a conscious act, and the reference drawing's own #128a52 —
    /// which does not pass — is one character away.
    /// </remarks>
    [Fact]
    public void The_light_accent_is_the_one_deviation_D6_chose()
        => _tokens["--accent"].Light.ShouldBe("#0f7a48");

    [Fact]
    public void An_explicit_theme_overrides_the_system_preference_in_both_directions()
    {
        _css.ShouldContain("color-scheme: light dark;");
        _css.ShouldContain(":root[data-theme='light']");
        _css.ShouldContain(":root[data-theme='dark']");
    }
}
