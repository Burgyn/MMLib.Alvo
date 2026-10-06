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
    /// The native controls the rule allows, by file: the static sign-in and set-password pages, whose inputs cannot be
    /// the library's (D10); the record form's reference combobox, which is Alvo's for its <c>aria-activedescendant</c> (D7); the
    /// command palette's search-and-go line, which is not a form field (§3.8).
    /// </summary>
    private static readonly string[] _nativeControlsAllowed =
        ["Shell/SignIn.razor", "Shell/SetPassword.razor", "Data/RecordForm.razor", "Shell/CommandPalette.razor", "Schema/Transfer.razor"];

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

    [Fact]
    public void An_id_that_is_only_part_of_another_fields_for_is_not_named_by_it()
    {
        const string source = """
            <Field Label="Rename" For="rename">x</Field>
            <MudTextField T="string" Variant="Variant.Outlined" id="name" />
            """;

        IsNamed(new Input("probe", Attributes(LibraryTags(source).Single())), source).ShouldBeFalse();
    }

    /// <summary>FieldEditor's default box: a computed For= with a string literal inside it and text after the literal.</summary>
    [Fact]
    public void A_computed_for_with_a_literal_inside_parses_whole_and_names_only_that_id()
    {
        const string source = """
            <Field Label="Default value" For="@(_facets.Type is FieldType.Boolean ? null : "new-field-default")"
                   LabelId="new-field-default-label">
                <MudTextField T="string" Variant="Variant.Outlined" id="new-field-default" />
                <MudTextField T="string" Variant="Variant.Outlined" id="new-field-defaults" />
            </Field>
            """;

        var field = Attributes(source[..RazorTag.End(source, 0)]);
        field["For"].ShouldBe("@(_facets.Type is FieldType.Boolean ? null : \"new-field-default\")");
        field["LabelId"].ShouldBe("new-field-default-label");
        RazorTag.Ids(field["For"]).ShouldContain("new-field-default");

        var inputs = LibraryTags(source).Select(tag => new Input("probe", Attributes(tag))).ToList();
        IsNamed(inputs[0], source).ShouldBeTrue();
        IsNamed(inputs[1], source).ShouldBeFalse("a mismatched id is not named by a For that computes another");
    }

    private static bool IsNamed(Input input, string source)
    {
        if (input.Attributes.ContainsKey("aria-label") || input.Attributes.ContainsKey("aria-labelledby"))
        {
            return true;
        }

        var ids = RazorTag.Ids(input.Attributes.GetValueOrDefault("id") ?? string.Empty);
        return ForAttribute().Matches(source)
            .Any(match => RazorTag.Ids(RazorTag.Value(source, match.Index + match.Length - 1)).Overlaps(ids));
    }

    /// <summary>Every <c>&lt;Field … For=…&gt;</c> block that has a <c>&lt;Hint&gt;</c>, to its matching close.</summary>
    private static IEnumerable<string> HintedFields(string source)
    {
        foreach (Match open in FieldOpen().Matches(source))
        {
            var end = RazorTag.End(source, open.Index);
            if (!Attributes(source[open.Index..end]).ContainsKey("For"))
            {
                continue;
            }

            var block = source[open.Index..FieldEnd(source, end)];
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

    /// <summary>Every opening tag of a library input, to its <c>&gt;</c> (<see cref="RazorTag.End"/>).</summary>
    private static IEnumerable<string> LibraryTags(string source)
        => LibraryOpen().Matches(source).Select(open => source[open.Index..RazorTag.End(source, open.Index)]);

    private static Dictionary<string, string> Attributes(string tag) => RazorTag.Attributes(tag);

    [GeneratedRegex(@"<(MudTextField|MudSelect|MudNumericField|MudAutocomplete)(?=[\s@/>])")]
    private static partial Regex LibraryOpen();

    /// <summary>A <c>for=</c> or <c>For=</c> attribute, up to and including its opening quote.</summary>
    [GeneratedRegex(@"(?<=\s)[Ff]or\s*=\s*""")]
    private static partial Regex ForAttribute();

    [GeneratedRegex(@"<Field(?=[\s>])")]
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
