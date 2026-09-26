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
            .Where(file => file.Name != "DesignSystem/ErrorPanel.razor" && Tags(file.Source, "AlvoAlert")
                .Any(tag => tag.GetValueOrDefault("Tone", string.Empty).Contains("AlertTone.Error", StringComparison.Ordinal)))
            .Select(file => file.Name)
            .ShouldBeEmpty("a refusal is an ErrorPanel with a Title, which is what says what could not be done");

    /// <summary>
    /// Every refusal is keyed per attempt by the one helper (<c>RefusalState</c>), never a counter of a screen's own
    /// (final review M12): a panel keyed by hand is a place the "every refusal takes focus again" rule can drift.
    /// </summary>
    [Fact]
    public void No_screen_keys_a_refusal_panel_by_hand()
        => Components()
            .Where(file => Tags(file.Source, "ErrorPanel").Any(tag => tag.ContainsKey("@key")))
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
            .SelectMany(editor => editor.Attributes.TryGetValue("SubmitText", out var submit)
                ? Literals(submit).Where(text => !_submitWords.Contains(text)).Select(text => $"{editor.File}: \"{text}\"")
                : [$"{editor.File}: no SubmitText"])
            .ShouldBeEmpty($"a submit says one of: {string.Join(", ", _submitWords)}");

    /// <summary>
    /// Enter submits every single-line form (spec §3.4; batch-B re-review N3): a single-line box is in a form (its own,
    /// or an editor's, whose fields a component with an <c>AlvoEditor</c> draws), unless it is a search over a list,
    /// which is named by an <c>aria-label</c> saying what it filters or searches and has nothing to submit (§3.8).
    /// </summary>
    [Fact]
    public void Every_single_line_box_is_in_a_form_Enter_submits()
        => Components()
            .Where(file => !file.Source.Contains("<AlvoEditor", StringComparison.Ordinal))
            .SelectMany(file => TagsAt(file.Source, "MudTextField")
                .Where(tag => !tag.Attributes.ContainsKey("Lines") && !IsSearch(tag.Attributes) && !InsideForm(file.Source, tag.Index))
                .Select(tag => $"{file.Name}: {tag.Attributes.GetValueOrDefault("id") ?? "(no id)"}"))
            .ShouldBeEmpty("Enter in a single-line box submits its form, so the box is in one");

    /// <summary>
    /// A create is found in one place per level (spec §3.7; re-review N3): every "New …" button is a page header's
    /// primary action or a section head's action, read from the source so a new one placed anywhere else fails.
    /// </summary>
    [Fact]
    public void Every_new_trigger_is_a_headers_primary_or_a_section_heads_action()
        => Components()
            .SelectMany(file => TagsAt(file.Source, "AlvoButton")
                .Where(tag => ButtonText(file.Source, tag.Index).StartsWith("New ", StringComparison.Ordinal))
                .Where(tag => !Within(file.Source, tag.Index, "Primary") && !Within(file.Source, tag.Index, "Actions"))
                .Select(tag => $"{file.Name}: {ButtonText(file.Source, tag.Index)}"))
            .ShouldBeEmpty("a create is in a page header's <Primary> or a section head's <Actions>");

    private static bool IsSearch(Dictionary<string, string> attributes)
        => attributes.GetValueOrDefault("aria-label") is { } name
           && (name.StartsWith("Filter ", StringComparison.Ordinal) || name.StartsWith("@($\"Search ", StringComparison.Ordinal)
               || name.StartsWith("Search ", StringComparison.Ordinal));

    /// <summary>Whether a <c>&lt;form</c> opened before <paramref name="index"/> is not closed by then.</summary>
    private static bool InsideForm(string source, int index)
        => source.LastIndexOf("<form", index, StringComparison.Ordinal) > source.LastIndexOf("</form>", index, StringComparison.Ordinal);

    /// <summary>Whether a <c>&lt;<paramref name="slot"/>&gt;</c> opened before <paramref name="index"/> is not closed by then.</summary>
    private static bool Within(string source, int index, string slot)
        => source.LastIndexOf($"<{slot}>", index, StringComparison.Ordinal)
           > source.LastIndexOf($"</{slot}>", index, StringComparison.Ordinal);

    /// <summary>The text between a button tag's end and its closing tag.</summary>
    private static string ButtonText(string source, int index)
    {
        var start = RazorTag.End(source, index);
        var end = source.IndexOf("</AlvoButton>", start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : source[start..end].Trim();
    }

    /// <summary>Every opening tag of <paramref name="name"/>, by its attributes, read as the Razor compiler reads them.</summary>
    private static IEnumerable<Dictionary<string, string>> Tags(string source, string name)
        => TagsAt(source, name).Select(tag => tag.Attributes);

    /// <summary>Every opening tag of <paramref name="name"/>, where it opens and its attributes (<c>=&gt;</c> included).</summary>
    private static IEnumerable<(int Index, Dictionary<string, string> Attributes)> TagsAt(string source, string name)
        => Regex.Matches(source, $@"<{name}(?=[\s@/>])")
            .Select(open => (open.Index, RazorTag.Attributes(source[open.Index..RazorTag.End(source, open.Index)])));

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

    /// <summary>
    /// Ctrl/Cmd+Enter has one mechanism, alvo.js's <c>form[data-alvo-chord-submit]</c> (spec §3.4; final review M5):
    /// no component reads the modifier keys itself, as the assistant, the import and the rule box each once did.
    /// </summary>
    [Fact]
    public void No_screen_handles_the_submit_chord_itself()
        => Sources()
            .Where(file => ModifierKey().IsMatch(file.Source))
            .Select(file => file.Name)
            .ShouldBeEmpty("the chord is alvo.js's, on a form marked data-alvo-chord-submit");

    /// <summary>
    /// Every multi-line box sits in a form the chord submits, an editor's or its own, and its hint says the chord in
    /// the one sentence <c>ChordHint</c> writes.
    /// </summary>
    [Fact]
    public void Every_multi_line_box_submits_by_the_chord_and_says_so()
        => Components()
            .Where(file => Tags(file.Source, "MudTextField").Any(tag => tag.ContainsKey("Lines")))
            .Select(file => (file.Name, Source: file.Source + CodeBehind(file.Name)))
            .Where(file => !(file.Source.Contains("data-alvo-chord-submit", StringComparison.Ordinal)
                    || file.Source.Contains("<AlvoEditor", StringComparison.Ordinal))
                || !file.Source.Contains("ChordHint.Of(", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ShouldBeEmpty("a multi-line box is submitted by the chord and its hint names it (ChordHint)");

    /// <summary>
    /// Every pane that reads again what it already shows says so at its top (spec §3.6; final review M9): the History
    /// list and revision, the Data grid, the Access people and Preview's plan.
    /// </summary>
    [Theory]
    [InlineData("History/History.razor")]
    [InlineData("Data/EntityData.razor")]
    [InlineData("Access/Access.razor")]
    [InlineData("Schema/Preview.razor")]
    public void A_pane_that_reads_again_shows_the_refresh_bar(string component)
        => Components().Single(file => file.Name == component).Source
            .ShouldContain("@RefreshBar.While(", Case.Sensitive, "the refresh indicator is RefreshBar, at the pane's top");

    /// <summary>
    /// Every confirm the dashboard draws has its focus after Cancel and after its verb pinned in a browser (spec §3.2;
    /// batch-B re-review N1): the confirms are read from the source, so a new one fails here until a scenario pins it.
    /// </summary>
    [Fact]
    public void Every_confirm_has_its_focus_after_closing_pinned()
    {
        var pinned = EndToEndSources()
            .SelectMany(source => PinnedConfirm().Matches(source).Select(match => match.Groups[1].Value))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        Confirms().Where(key => pinned.GetValueOrDefault(key) < 2)
            .ShouldBeEmpty("a confirm's Cancel and verb both need a FocusAfterConfirmAsync in a scenario");
    }

    /// <summary>
    /// Every confirm, by its test id; the discard confirm, drawn in two places, by where (<c>discard-sheet@PendingBar</c>).
    /// </summary>
    private static List<string> Confirms()
    {
        var confirms = new List<string>();
        foreach (var file in Components().Where(file => file.Name != "Schema/DiscardConfirm.razor"))
        {
            confirms.AddRange(ConfirmOpen().Matches(file.Source).Select(open =>
                RazorTag.Attributes(file.Source[open.Index..RazorTag.End(file.Source, open.Index)]).GetValueOrDefault("TestId")
                    ?? $"(no TestId) {file.Name}"));
            confirms.AddRange(DiscardOpen().Matches(file.Source)
                .Select(_ => $"discard-sheet@{Path.GetFileNameWithoutExtension(file.Name)}"));
        }

        return confirms;
    }

    private static IEnumerable<string> EndToEndSources()
        => Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "test", "MMLib.Alvo.Admin.Tests.EndToEnd"), "*.cs")
            .Select(File.ReadAllText);

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

    /// <summary>A component's <c>.razor.cs</c>, or nothing when its code is all in the markup.</summary>
    private static string CodeBehind(string component)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components", component + ".cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>Every component's markup and code, by its path under <c>Components/</c>.</summary>
    private static IEnumerable<(string Name, string Source)> Sources()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    /// <summary>
    /// Every component's markup, by its path under <c>Components/</c>, without its Razor comments: a remark that names
    /// <c>&lt;AlvoEditor&gt;</c> is not an editor, and read as one it hid a real tag's attributes behind an empty one.
    /// </summary>
    internal static IEnumerable<(string Name, string Source)> Components()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'),
                RazorComment().Replace(File.ReadAllText(path), string.Empty)));
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"\b(CtrlKey|MetaKey)\b")]
    private static partial Regex ModifierKey();

    [GeneratedRegex(@"<AlvoConfirm(?=[\s@/>])")]
    private static partial Regex ConfirmOpen();

    [GeneratedRegex(@"<DiscardConfirm(?=[\s@/>])")]
    private static partial Regex DiscardOpen();

    [GeneratedRegex(@"FocusAfterConfirmAsync\(\s*""([^""]+)""")]
    private static partial Regex PinnedConfirm();

    [GeneratedRegex(@"<AlvoEditor(?=[\s@/>])")]
    private static partial Regex EditorOpen();

    /// <summary>A C# string literal, interpolated or not, read whole from its opening quote to its closing one.</summary>
    [GeneratedRegex(@"\$?""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();
}
