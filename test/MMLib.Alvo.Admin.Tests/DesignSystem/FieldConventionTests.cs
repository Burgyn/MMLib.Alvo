using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// Every text input is written the one way spec §3.8 draws it: an outlined library box with no name or hint of its
/// own, named by a <c>Field</c> above it (or by <c>aria-label</c> / <c>aria-labelledby</c>) and described by the
/// Field's hint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the library's <c>Label</c> is refused rather than restyled.</b> An outlined MudTextField with a label draws it
/// in a notch cut into its border, sized by a classless <c>legend</c>, and floats it into the box when the box is empty.
/// That is the look the dashboard had three of in one editor, and the notch is what struck through the typed-name
/// confirm's label. Its helper text sits in a row padded by an <c>!important</c> utility class, which no rule of
/// alvo.css can outrank from a layer above the library. A name and a hint the library never draws cannot be drawn
/// any of those ways.
/// </para>
/// <para>
/// <b>What a text scan cannot see</b>, and what covers it instead: an attribute whose name is computed at run time (so
/// <c>@attributes</c> splatting on a library input is refused outright); a library input opened from a
/// <c>RenderTreeBuilder</c> with a type held in a variable (a literal <c>OpenComponent&lt;MudTextField</c> is refused);
/// and whether a name that is there is the right one. <c>FieldConsistencyScenarios</c> measures the drawn result in a
/// browser on every screen with fields.
/// </para>
/// </remarks>
public sealed partial class FieldConventionTests
{
    private static readonly string[] _inputs = ["MudTextField", "MudSelect", "MudNumericField", "MudAutocomplete"];

    private static readonly string[] _refused = ["Label", "HelperText", "Margin", "Dense", "@attributes"];

    /// <summary>
    /// The native controls the rule allows, by file: the static sign-in page, whose inputs cannot be the library's
    /// (D10); the record form's reference combobox, which is Alvo's for its <c>aria-activedescendant</c> (D7); the
    /// command palette's search-and-go line, which is not a form field (§3.8); and the typed-name confirm Task 11
    /// deletes.
    /// </summary>
    private static readonly string[] _nativeControlsAllowed =
        ["Shell/SignIn.razor", "Data/RecordForm.razor", "Shell/CommandPalette.razor", "DesignSystem/ConfirmByName.razor"];

    [Fact]
    public void No_library_input_names_itself_or_carries_its_own_hint_or_density()
        => LibraryInputs()
            .SelectMany(input => _refused.Where(input.Attributes.ContainsKey)
                .Select(attribute => $"{input.File}: {input.Id}: {attribute}"))
            .ShouldBeEmpty("a field's name and hint are the Field's (spec §3.8), and its density is alvo.css's");

    [Fact]
    public void Every_library_input_is_outlined()
        => LibraryInputs()
            .Where(input => input.Attributes.GetValueOrDefault("Variant") != "Variant.Outlined")
            .Select(input => $"{input.File}: {input.Id}")
            .ShouldBeEmpty("alvo.css draws the one box on the outlined variant");

    /// <summary>A library input carries no name of its own, so it has to be given one.</summary>
    [Fact]
    public void Every_library_input_has_a_name_a_screen_reader_can_read()
        => Components()
            .SelectMany(file => LibraryInputs(file).Where(input => !IsNamed(input, file.Source))
                .Select(input => $"{file.Name}: {input.Id}"))
            .ShouldBeEmpty("a Field For= its id, aria-label or aria-labelledby (spec §3.8)");

    /// <summary>The Field gives its hint the id <c>{For}-hint</c>; the input inside has to point at it.</summary>
    [Fact]
    public void Every_input_under_a_hinted_field_is_described_by_its_hint()
        => Components()
            .SelectMany(file => HintedFields(file.Source)
                .Where(block => LibraryTags(block).FirstOrDefault() is { } tag && !Attributes(tag).ContainsKey("aria-describedby"))
                .Select(block => $"{file.Name}: {block.Split('\n')[0].Trim()}"))
            .ShouldBeEmpty("write aria-describedby=\"{For}-hint\" on the input (spec §3.8)");

    [Fact]
    public void A_native_control_is_drawn_only_where_the_library_cannot_draw_one()
        => Components()
            .Where(file => !_nativeControlsAllowed.Contains(file.Name))
            .Where(file => NativeControl().IsMatch(file.Source))
            .Select(file => file.Name)
            .ShouldBeEmpty("an <input>, <textarea> or <select> in markup is a second look (spec §3.8)");

    [Fact]
    public void No_library_input_is_built_in_code_where_this_scan_cannot_read_its_attributes()
        => Directory.EnumerateFiles(AdminRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => _inputs.Any(name => File.ReadAllText(path).Contains($"OpenComponent<{name}", StringComparison.Ordinal)))
            .ShouldBeEmpty();

    [Fact]
    public void The_scan_reads_a_tag_to_its_end_across_a_lambda_lines_and_a_ref()
    {
        const string source = """
            <MudTextField
                @ref="_box" T="string" ValueChanged="text => _x = text" AdornmentAriaLabel="Clear"
                          Label="Name" id="probe" />
            <p>after</p>
            """;

        var tag = LibraryTags(source).ShouldHaveSingleItem();
        tag.ShouldNotContain("after");
        var attributes = Attributes(tag);
        attributes.ShouldContainKey("Label");
        attributes.ShouldContainKey("AdornmentAriaLabel");
        attributes["id"].ShouldBe("probe");
    }

    [Fact]
    public void A_name_is_found_through_a_for_that_computes_the_same_id()
    {
        const string source = """
            <label for="@(column.Type == FieldType.Enum ? null : ControlId(column))">x</label>
            <MudTextField T="string" Variant="Variant.Outlined" id="@ControlId(column)" />
            <MudTextField T="string" Variant="Variant.Outlined" id="orphan" />
            """;

        var inputs = LibraryTags(source).Select(tag => new Input("probe", Attributes(tag))).ToList();
        IsNamed(inputs[0], source).ShouldBeTrue();
        IsNamed(inputs[1], source).ShouldBeFalse();
    }

    private static bool IsNamed(Input input, string source)
    {
        if (input.Attributes.ContainsKey("aria-label") || input.Attributes.ContainsKey("aria-labelledby"))
        {
            return true;
        }

        var id = Bare(input.Attributes.GetValueOrDefault("id") ?? string.Empty);
        return id.Length > 0 && ForValues().Matches(source).Any(match => Bare(match.Groups["value"].Value).Contains(id, StringComparison.Ordinal));
    }

    /// <summary>An attribute value without Razor's <c>@</c> or <c>@( … )</c> around it.</summary>
    private static string Bare(string value)
    {
        var bare = value.StartsWith('@') ? value[1..] : value;
        return bare.StartsWith('(') && bare.EndsWith(')') ? bare[1..^1] : bare;
    }

    /// <summary>Every <c>&lt;Field … For=…&gt;</c> block that has a <c>&lt;Hint&gt;</c>, to its matching close.</summary>
    private static IEnumerable<string> HintedFields(string source)
    {
        foreach (Match open in FieldOpen().Matches(source))
        {
            if (!Attributes(open.Value).ContainsKey("For"))
            {
                continue;
            }

            var block = source[open.Index..FieldEnd(source, open.Index + open.Length)];
            if (block.Contains("<Hint>", StringComparison.Ordinal))
            {
                yield return block;
            }
        }
    }

    private static int FieldEnd(string source, int from)
    {
        var depth = 1;
        foreach (Match tag in FieldTag().Matches(source, from))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0)
            {
                return tag.Index + tag.Length;
            }
        }

        return source.Length;
    }

    private static IEnumerable<Input> LibraryInputs()
        => Components().SelectMany(LibraryInputs);

    private static IEnumerable<Input> LibraryInputs((string Name, string Source) file)
        => LibraryTags(file.Source).Select(tag => new Input(file.Name, Attributes(tag)));

    private static IEnumerable<(string Name, string Source)> Components()
    {
        var root = Path.Combine(AdminRoot(), "Components");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    private static string AdminRoot() => Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin");

    /// <summary>Every opening tag of a library input, to the first <c>&gt;</c> outside a quoted value.</summary>
    private static IEnumerable<string> LibraryTags(string source)
        => LibraryOpen().Matches(source).Select(open => source[open.Index..TagEnd(source, open.Index)]);

    private static int TagEnd(string source, int start)
    {
        var quoted = false;
        for (var at = start; at < source.Length; at++)
        {
            if (source[at] == '"')
            {
                quoted = !quoted;
            }
            else if (source[at] == '>' && !quoted)
            {
                return at + 1;
            }
        }

        return source.Length;
    }

    /// <summary>A tag's attributes by name, each name a whole token: <c>AdornmentAriaLabel</c> is not <c>Label</c>.</summary>
    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match attribute in Attribute().Matches(tag))
        {
            attributes.TryAdd(attribute.Groups["name"].Value, attribute.Groups["value"].Value);
        }

        return attributes;
    }

    [GeneratedRegex(@"<(MudTextField|MudSelect|MudNumericField|MudAutocomplete)(?=[\s@/>])")]
    private static partial Regex LibraryOpen();

    [GeneratedRegex(@"(?<=[\s])(?<name>@?[A-Za-z][\w:.-]*)\s*=\s*""(?<value>(?:[^""]|""(?=[^""\s>]*""\)))*)""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"\b[Ff]or=""(?<value>(?:[^""]|""(?=[^""\s>]*""\)))*)""")]
    private static partial Regex ForValues();

    [GeneratedRegex(@"<Field(?=[\s>])[^>]*>")]
    private static partial Regex FieldOpen();

    [GeneratedRegex(@"</?Field(?=[\s>])")]
    private static partial Regex FieldTag();

    [GeneratedRegex(@"<(input|textarea|select)(?=[\s/>])")]
    private static partial Regex NativeControl();

    private sealed record Input(string File, Dictionary<string, string> Attributes)
    {
        public string Id => Attributes.GetValueOrDefault("id") ?? "(no id)";
    }
}
