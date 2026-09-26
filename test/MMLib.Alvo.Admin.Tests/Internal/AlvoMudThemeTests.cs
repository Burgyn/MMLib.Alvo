using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>The library's palette is Alvo's tokens, value for value, in both themes (spec §6: no second theme).</summary>
public sealed class AlvoMudThemeTests
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> _tokens =
        Stylesheet.ReadThemedTokens(File.ReadAllText(Stylesheet.AlvoCssPath));

    [Fact]
    public void Every_colour_the_theme_carries_is_the_stylesheet_token_of_the_same_name()
    {
        AlvoMudTheme.Colours.ShouldNotBeEmpty();
        foreach (var colour in AlvoMudTheme.Colours)
        {
            _tokens.ShouldContainKey(colour.Token);
            colour.Light.ShouldBe(_tokens[colour.Token].Light, $"{colour.Token}, light");
            colour.Dark.ShouldBe(_tokens[colour.Token].Dark, $"{colour.Token}, dark");
        }
    }

    [Theory]
    [InlineData("--accent", "--accentText")]
    [InlineData("--danger-fg", "--panel")]
    [InlineData("--ok-fg", "--accentText")]
    [InlineData("--warn-fg", "--panel")]
    [InlineData("--neutral-fg", "--panel")]
    public void Text_on_a_filled_button_or_snackbar_meets_AA_in_both_themes(string fill, string text)
    {
        Stylesheet.ContrastRatio(Hex(fill).Light, Hex(text).Light).ShouldBeGreaterThanOrEqualTo(4.5);
        Stylesheet.ContrastRatio(Hex(fill).Dark, Hex(text).Dark).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void The_dark_palette_is_scoped_to_the_resolved_theme_attribute()
    {
        AlvoMudTheme.DarkScope.ShouldBe(":root[data-theme=dark]");
        AlvoMudTheme.DarkScoped.PseudoCss.Scope.ShouldBe(AlvoMudTheme.DarkScope);
    }

    [Fact]
    public void The_type_is_Alvos_and_nothing_shouts()
    {
        var type = AlvoMudTheme.Light.Typography;
        type.Default.FontFamily.ShouldNotBeNull().First().ShouldBe("Public Sans");
        type.Default.FontWeight.ShouldBe("500");
        type.Button.TextTransform.ShouldBe("none");
        AlvoMudTheme.Light.LayoutProperties.DefaultBorderRadius.ShouldBe("6px");
    }

    private static (string Light, string Dark) Hex(string token)
    {
        var colour = AlvoMudTheme.Colours.Single(entry => entry.Token == token);
        return (colour.Light, colour.Dark);
    }
}
