using MMLib.Alvo.Admin.Tests.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// What the component layer may not do — the rules that keep <see cref="DesignTokenTests"/>
/// meaningful.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a literal colour is a defect rather than a shortcut.</b> The contrast facts measure
/// tokens. A component that writes its own colour is invisible to them, so one literal is enough
/// to make "every foreground pair meets AA" true of the stylesheet and false of the product. The
/// same argument covers dark mode: a token carries both themes by construction, a literal carries
/// one.
/// </para>
/// </remarks>
public sealed partial class ComponentLayerTests
{
    private const string LayerStatement = "@layer tokens, base, layout, components, utilities;";

    private static readonly string _css = File.ReadAllText(Stylesheet.AlvoCssPath);

    private static readonly string[] _layers = LayerStatement["@layer ".Length..^1].Split(", ");

    [Fact]
    public void The_component_layer_uses_tokens_rather_than_literal_colours()
        => Stylesheet.LiteralColoursOutsideTokens(_css).ShouldBeEmpty();

    /// <summary>
    /// No class means two things.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is silent by construction: the later definition wins, the page
    /// still renders, and the control still works — it is simply drawn as something else. It cost a
    /// checkbox label being rendered as an 18 px box over the field beneath it, and nothing but a
    /// screenshot could have caught it.
    /// </remarks>
    [Fact]
    public void No_selector_is_defined_twice()
        => Stylesheet.SelectorsDefinedTwice(_css).ShouldBeEmpty();

    [Fact]
    public void The_focus_ring_is_never_removed()
    {
        _css.ShouldContain(":focus-visible");
        _css.ShouldNotContain("outline: none !important");
    }

    /// <summary>
    /// The preference is not about speed, so a shortened animation does not honour it.
    /// </summary>
    [Fact]
    public void Reduced_motion_disables_animation_rather_than_shortening_it()
    {
        var block = _css[_css.IndexOf("prefers-reduced-motion", StringComparison.Ordinal)..];
        var rule = block[..block.IndexOf('}', StringComparison.Ordinal)];
        rule.ShouldContain("animation: none");
        rule.ShouldContain("transition: none");
    }

    /// <summary>
    /// Design §4.1: a warned subsystem and a refused feature are two different states, and a UI
    /// that draws them alike invites an author to use a control whose only possible output is a
    /// descriptor the apply rejects.
    /// </summary>
    [Fact]
    public void Warned_and_refused_are_two_different_classes()
    {
        _css.ShouldContain(".a-notyet");
        _css.ShouldContain(".a-refused");
        _css.ShouldContain(".a-refused__reason");
    }

    [Fact]
    public void Numeric_columns_are_tabular()
        => _css.ShouldContain("font-variant-numeric: tabular-nums");

    /// <summary>
    /// <c>.a-mono</c> names a family and leaves size and colour to wherever it is used.
    /// </summary>
    /// <remarks>
    /// A utility that also set 11 px and the dim colour was the last word on every element it joined —
    /// declared after <c>.a-page-title</c>, it drew an entity's name in its own page heading at 11 px
    /// (finding D-1). The small dim look is <c>.a-ident</c>, which says so in its name.
    /// </remarks>
    [Fact]
    public void The_mono_utility_sets_the_family_and_nothing_else_a_context_decides()
    {
        var rule = TopLevelRule(".a-mono");
        rule.ShouldContain("font-family: var(--font-mono)");
        rule.ShouldNotContain("font-size:");
        rule.ShouldNotContain("color:");
    }

    /// <summary>
    /// The cascade's order is one statement, and it names utilities last.
    /// </summary>
    /// <remarks>
    /// Source order was the only policy before it (finding F-20), and D-1 is what that policy cost: a
    /// utility that happened to be declared below a component's rule overrode it. The layer a rule sits in
    /// now decides, whatever its specificity or position.
    /// </remarks>
    [Fact]
    public void The_cascade_is_ordered_by_one_layer_statement()
    {
        _css.ShouldContain(LayerStatement);
        _css.IndexOf(LayerStatement, StringComparison.Ordinal)
            .ShouldBeLessThan(_css.IndexOf(":root {", StringComparison.Ordinal));
    }

    /// <summary>
    /// No rule sits outside a layer.
    /// </summary>
    /// <remarks>
    /// An unlayered rule outranks every layered one, so a single rule added at the top level would quietly
    /// bring source order back for whatever it touches — and win over a utility it was meant to lose to.
    /// </remarks>
    [Fact]
    public void Every_rule_sits_in_a_declared_layer()
        => Stylesheet.BlocksOutsideLayers(_css, _layers).ShouldBeEmpty();

    /// <summary>
    /// The layout utilities are in the last layer, so they win over any component rule they are added to —
    /// the reason they can replace a <c>style</c> attribute, which won the same way.
    /// </summary>
    [Fact]
    public void The_layout_utilities_are_the_last_word()
    {
        var css = _css.ReplaceLineEndings("\n");
        var start = css.IndexOf("\n@layer utilities {", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the stylesheet has no utilities layer");
        var utilities = css[start..];

        utilities.ShouldContain("  .a-spacer {");
        utilities.ShouldContain("  .a-row--gap-2 {");
        utilities.ShouldContain("  .a-row--gap-3 {");
    }

    /// <summary>
    /// Every face the stylesheet declares has a file behind it.
    /// </summary>
    /// <remarks>
    /// A missing font fails silently by design — <c>font-display: swap</c> keeps the fallback on
    /// screen — so a renamed or dropped file would ship a dashboard drawn in system-ui with nothing
    /// red anywhere.
    /// </remarks>
    [Fact]
    public void Every_font_the_stylesheet_names_is_shipped_beside_it()
    {
        var fonts = Path.Combine(Path.GetDirectoryName(Stylesheet.AlvoCssPath)!, "fonts");
        var named = FontUrl().Matches(_css).Select(match => match.Groups["file"].Value).ToList();

        named.ShouldNotBeEmpty();
        named.Where(file => !File.Exists(Path.Combine(fonts, file))).ShouldBeEmpty();
    }

    /// <summary>The body of the one top-level rule for <paramref name="selector"/>.</summary>
    /// <param name="selector">The selector, exactly as it opens its rule.</param>
    /// <returns>The declarations between its braces.</returns>
    private static string TopLevelRule(string selector)
    {
        var css = Stylesheet.Unlayered(_css);
        var start = css.IndexOf($"\n{selector} {{", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{selector} has no top-level rule");
        var body = css[(start + selector.Length + 3)..];
        return body[..body.IndexOf('}', StringComparison.Ordinal)];
    }

    [GeneratedRegex(@"url\('fonts/(?<file>[^']+)'\)")]
    private static partial Regex FontUrl();
}
