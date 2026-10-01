using MMLib.Alvo.Admin.Tests.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// <b>One look for "the apply refuses this"</b> (final branch review, item 11): the danger tone, wherever it is drawn —
/// the <c>&lt;Refusal&gt;</c> sentence, the Hooks tab's folded refusals, a field row's badge and the Overview's list of
/// refusals no screen places.
/// </summary>
/// <remarks>
/// The warning tone is the other state's, "declared, not honoured by this build" (design §4.1: warned is not refused).
/// The same refused slot was drawn amber on the Overview and red on the Fields tab, so the two states read alike on one
/// screen and apart on the next.
/// </remarks>
public sealed partial class RefusedToneTests
{
    private static readonly string _css = File.ReadAllText(Stylesheet.AlvoCssPath);

    [Theory]
    [InlineData(".a-refused__reason {")]
    [InlineData(".a-refused--fold > summary {")]
    public void A_refusals_words_are_drawn_in_the_danger_tone(string selector)
    {
        var start = _css.IndexOf(selector, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{selector} is defined");
        var rule = _css[start.._css.IndexOf('}', start)];

        rule.ShouldContain("color: var(--danger-fg)");
        rule.ShouldNotContain("--warn-fg");
    }

    [Fact]
    public void Every_badge_that_names_a_refusal_is_a_danger_badge()
        => PatternLanguageTests.Components()
            .SelectMany(file => Badge().Matches(file.Source)
                .Where(badge => NamesARefusal(badge.Value) && !IsDanger(badge))
                .Select(badge => $"{file.Name}: {badge.Value}"))
            .ShouldBeEmpty("a refused slot is drawn in the danger tone, as <Refusal> and the Fields tab draw it");

    private static bool IsDanger(Match badge)
        => badge.Groups["tag"].Value.Contains("a-badge--danger", StringComparison.Ordinal);

    private static bool NamesARefusal(string badge)
        => badge.Contains("refusal", StringComparison.OrdinalIgnoreCase) || badge.Contains("Refused", StringComparison.Ordinal);

    /// <summary>A badge's opening tag and its content, to its closing tag.</summary>
    [GeneratedRegex(@"(?<tag><span\b[^>]*\ba-badge\b[^>]*>)[^<]*</span>")]
    private static partial Regex Badge();
}
