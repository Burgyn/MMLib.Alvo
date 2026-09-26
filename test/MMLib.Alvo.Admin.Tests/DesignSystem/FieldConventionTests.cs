namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// Every text input is written the one way spec §3.8 draws it: an outlined library box with no name or hint of its
/// own, inside a <c>Field</c> that names it from above and links its hint.
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
/// <c>FieldConsistencyScenarios</c> measures the result in a browser; this is the half that fails at build time,
/// naming the file and the input.
/// </para>
/// </remarks>
public sealed class FieldConventionTests
{
    private static readonly string[] _inputs = ["MudTextField", "MudSelect", "MudNumericField", "MudAutocomplete"];

    private static readonly string[] _refused = ["Label=", "HelperText=", "Margin=", "Dense="];

    /// <summary>
    /// The native inputs the rule allows: the static sign-in page, whose inputs cannot be the library's (D10); the
    /// record form's reference combobox, which is Alvo's for its <c>aria-activedescendant</c> (D7); and the typed-name
    /// confirm Task 11 deletes.
    /// </summary>
    private static readonly string[] _nativeInputsAllowed =
        ["Shell/SignIn.razor", "Data/RecordForm.razor", "DesignSystem/ConfirmByName.razor"];

    [Fact]
    public void No_library_input_names_itself_or_carries_its_own_hint_or_density()
        => LibraryInputs()
            .SelectMany(input => _refused.Where(attribute => input.Tag.Contains(attribute, StringComparison.Ordinal))
                .Select(attribute => $"{input.File}: {input.Id}: {attribute}"))
            .ShouldBeEmpty("a field's name and hint are the Field's (spec §3.8), and its density is alvo.css's");

    [Fact]
    public void Every_library_input_is_outlined()
        => LibraryInputs()
            .Where(input => !input.Tag.Contains("Variant=\"Variant.Outlined\"", StringComparison.Ordinal))
            .Select(input => $"{input.File}: {input.Id}")
            .ShouldBeEmpty("alvo.css draws the one box on the outlined variant");

    [Fact]
    public void A_native_text_input_is_drawn_only_where_the_library_cannot_draw_one()
        => Components()
            .Where(file => !_nativeInputsAllowed.Contains(file.Name))
            .Where(file => file.Source.Contains("class=\"a-input\"", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ShouldBeEmpty();

    [Fact]
    public void The_scan_reads_a_tag_to_its_end_across_a_lambda_and_lines()
    {
        const string source = """
            <MudTextField T="string" ValueChanged="text => _x = text"
                          Label="Name" id="probe" />
            <p>after</p>
            """;

        var tag = Tags(source, "MudTextField").ShouldHaveSingleItem();
        tag.ShouldContain("Label=\"Name\"");
        tag.ShouldNotContain("after");
    }

    private static IEnumerable<(string File, string Id, string Tag)> LibraryInputs()
        => Components().SelectMany(file => _inputs.SelectMany(name => Tags(file.Source, name))
            .Select(tag => (file.Name, IdOf(tag), tag)));

    private static IEnumerable<(string Name, string Source)> Components()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "Components");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    /// <summary>Every opening tag named <paramref name="name"/>, to the first <c>&gt;</c> outside a quoted value.</summary>
    private static IEnumerable<string> Tags(string source, string name)
    {
        var open = $"<{name} ";
        for (var start = source.IndexOf(open, StringComparison.Ordinal); start >= 0;
             start = source.IndexOf(open, start + 1, StringComparison.Ordinal))
        {
            yield return source[start..TagEnd(source, start)];
        }
    }

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

    private static string IdOf(string tag)
    {
        var at = tag.IndexOf(" id=\"", StringComparison.Ordinal);
        return at < 0 ? tag.Split('\n')[0].Trim() : tag[(at + 5)..tag.IndexOf('"', at + 5)];
    }
}
