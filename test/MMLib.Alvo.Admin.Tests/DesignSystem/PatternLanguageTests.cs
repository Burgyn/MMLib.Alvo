using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// The pattern language (spec §3) is one behaviour on every screen, so each rule here scans every component rather
/// than one: a screen that departs from it fails here before anyone sees it.
/// </summary>
/// <remarks>
/// What a text scan cannot see (whether the behaviour is right in a browser) the end-to-end scenarios measure; what
/// this pins is that no screen writes its own copy of a pattern the design system already has.
/// </remarks>
public sealed partial class PatternLanguageTests
{
    /// <summary>
    /// A refusal is the titled <c>ErrorPanel</c>, never a bare error alert (spec §3.3; final review M2): an index's or
    /// a hook's refusal said what was wrong with no headline saying what could not be done.
    /// </summary>
    [Fact]
    public void Every_refusal_is_the_titled_error_panel()
        => Components()
            .Where(file => file.Name != "DesignSystem/ErrorPanel.razor" && ErrorAlert().IsMatch(file.Source))
            .Select(file => file.Name)
            .ShouldBeEmpty("a refusal is an ErrorPanel with a Title, which is what says what could not be done");

    /// <summary>
    /// Every refusal is keyed per attempt by the one helper (<c>RefusalState</c>), never a counter of a screen's own
    /// (final review M12): a panel keyed by hand is a place the "every refusal takes focus again" rule can drift.
    /// </summary>
    [Fact]
    public void No_screen_keys_a_refusal_panel_by_hand()
        => Components()
            .Where(file => KeyedPanel().IsMatch(file.Source))
            .Select(file => file.Name)
            .ShouldBeEmpty("a refusal is drawn by RefusalPanel.Of, which keys it per attempt");

    /// <summary>Every component's markup, by its path under <c>Components/</c>.</summary>
    internal static IEnumerable<(string Name, string Source)> Components()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    [GeneratedRegex(@"<AlvoAlert\b[^>]*AlertTone\.Error")]
    private static partial Regex ErrorAlert();

    [GeneratedRegex(@"<ErrorPanel\s[^>]*@key=")]
    private static partial Regex KeyedPanel();
}
