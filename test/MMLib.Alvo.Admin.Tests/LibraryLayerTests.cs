using MMLib.Alvo.Admin.Tests.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// MudBlazor's stylesheet sits beneath every Alvo rule (spec D8), and nothing is fetched from another origin.
/// </summary>
/// <remarks>
/// Mud's reset (<c>*{margin:0;padding:0;border-width:0}</c>, <c>button:focus{outline:none}</c>) is unlayered, and an
/// unlayered rule beats every layered one: without the layer, every screen not yet migrated would lose its spacing
/// and its focus ring the moment the package is referenced (study §6.2).
/// </remarks>
public sealed partial class LibraryLayerTests
{
    private static readonly string _wwwroot = Path.GetDirectoryName(Stylesheet.AlvoCssPath)!;

    private static readonly string _layered = File.ReadAllText(Path.Combine(_wwwroot, "alvo-mud.css"));

    private static readonly string _document = File.ReadAllText(
        Path.Combine(Stylesheet.AdminSourcePath, "Components", "AdminApp.razor"));

    [Fact]
    public void The_library_is_imported_into_the_lowest_layer_and_the_sheet_says_nothing_else()
        => Statements(_layered).ShouldBe(
        [
            "@layer mud, tokens, base, layout, components, utilities;",
            "@import url('../MudBlazor/MudBlazor.min.css') layer(mud);",
        ]);

    [Fact]
    public void The_design_systems_own_layer_order_is_the_tail_of_the_librarys()
        => File.ReadAllText(Stylesheet.AlvoCssPath)
            .ShouldContain("@layer tokens, base, layout, components, utilities;");

    [Fact]
    public void The_document_links_the_layered_library_before_the_design_system_and_never_the_library_itself()
    {
        _document.IndexOf("LibraryAssets.StyleSheet", StringComparison.Ordinal)
            .ShouldBeLessThan(_document.IndexOf("AlvoAdminAssets.StyleSheet", StringComparison.Ordinal));
        _document.ShouldNotContain("MudBlazor.min.css");
        _document.ShouldContain("LibraryAssets.Script");
    }

    [Fact]
    public void Nothing_the_document_or_the_stylesheets_load_comes_from_another_origin()
    {
        foreach (var text in new[] { _document, _layered, File.ReadAllText(Stylesheet.AlvoCssPath) })
        {
            text.ShouldNotContain("http://");
            text.ShouldNotContain("https://");
        }
    }

    private static string[] Statements(string css)
        => Comment().Replace(css, string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
