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

    /// <summary>
    /// One submit wording per kind of editor (spec §3.1; final review M1): what applies at once is created with "Create
    /// &lt;thing&gt;" and edited with "Save changes"; what is staged is added with "Add to the working copy" and edited
    /// with "Save to the working copy". The one other is the rename that warns it discards an unsaved rule.
    /// </summary>
    [Fact]
    public void Every_editor_submit_says_what_its_kind_of_edit_does()
        => Editors()
            .SelectMany(editor => Literals(editor.Attributes.GetValueOrDefault("SubmitText") ?? string.Empty)
                .Where(text => !_submitWords.Contains(text))
                .Select(text => $"{editor.File}: \"{text}\""))
            .ShouldBeEmpty($"a submit says one of: {string.Join(", ", _submitWords)}");

    /// <summary>An editor's title says whether it creates or edits: "New …" or "Edit …" (spec §3.1).</summary>
    [Fact]
    public void Every_editor_title_is_new_or_edit()
        => Editors()
            .SelectMany(editor => Literals(editor.Attributes.GetValueOrDefault("Title") ?? string.Empty)
                .Where(text => !text.StartsWith("New ", StringComparison.Ordinal)
                    && !text.StartsWith("Edit ", StringComparison.Ordinal)
                    && !text.StartsWith("Rename ", StringComparison.Ordinal))
                .Select(text => $"{editor.File}: \"{text}\""))
            .ShouldBeEmpty("an editor is titled New … or Edit … (a rename is titled by its verb)");

    private static readonly string[] _submitWords =
    [
        "Create record", "Create person", "Save changes", "Add to the working copy", "Save to the working copy",
        "Discard the unsaved rule and rename",
    ];

    /// <summary>Every <c>AlvoEditor</c> opening tag, with its attributes.</summary>
    private static IEnumerable<(string File, Dictionary<string, string> Attributes)> Editors()
        => Components().SelectMany(file => EditorOpen().Matches(file.Source)
            .Select(open => (file.Name, RazorTag.Attributes(file.Source[open.Index..RazorTag.End(file.Source, open.Index)]))));

    /// <summary>The words an attribute can show: the value, or every string literal in its C#.</summary>
    private static IEnumerable<string> Literals(string value)
        => !value.StartsWith('@')
            ? value.Length == 0 ? [] : [value]
            : StringLiteral().Matches(value).Select(match => match.Groups[1].Value);

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

    [GeneratedRegex(@"<AlvoEditor(?=[\s@/>])")]
    private static partial Regex EditorOpen();

    /// <summary>A C# string literal, interpolated or not, read whole from its opening quote to its closing one.</summary>
    [GeneratedRegex(@"\$?""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();
}
