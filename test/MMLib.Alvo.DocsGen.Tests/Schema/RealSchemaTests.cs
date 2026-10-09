using MMLib.Alvo.DocsGen.Schema;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public partial class RealSchemaTests
{
    private readonly SchemaWalker _walker = new(RealSchema.Load());
    private readonly IReadOnlyList<GeneratedPage> _pages = SchemaReferenceRenderer.Render(RealSchema.Load());

    [Fact]
    public void Every_top_level_property_has_a_page_or_an_index_section()
    {
        var index = Content("descriptor/index.md");
        var missing = _walker.TopLevelKeys
            .Where(key => _walker.IsBlock(key)
                ? _pages.All(page => page.RelativePath != $"descriptor/{SchemaReferenceRenderer.PageSlug(key)}.md")
                : !index.Contains($"### `{key}`", StringComparison.Ordinal))
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void No_key_lacks_a_description()
    {
        var undescribed = Blocks()
            .SelectMany(_walker.KeysOf)
            .Where(key => key.Description.Length == 0 && !key.IsConstant)
            .Select(key => key.Path)
            .ToList();

        undescribed.ShouldBeEmpty();
    }

    [Fact]
    public void No_conditional_is_unrecognised()
    {
        foreach (var block in Blocks())
        {
            _walker.KeysOf(block);
        }

        _walker.UnrecognisedConditions.ShouldBeEmpty();
    }

    [Fact]
    public void Anchors_are_unique_per_page()
    {
        foreach (var page in DescriptorPages())
        {
            AnchorId().Matches(page.Content).Select(match => match.Groups[1].Value).ShouldBeUnique(page.RelativePath);
        }
    }

    [Fact]
    public void No_page_exceeds_the_key_section_cap()
    {
        var oversized = DescriptorPages()
            .Select(page => (page.RelativePath, Sections: KeySection().Matches(page.Content).Count))
            .Where(page => page.Sections > SchemaReferenceRenderer.MaxKeySectionsPerPage)
            .ToList();

        oversized.ShouldBeEmpty("Split the page by its next sub-object in DescriptorPageSplit; never raise the cap.");
    }

    [Theory]
    [InlineData("descriptor/entities.md", 10)]
    [InlineData("descriptor/entities-fields.md", 11)]
    [InlineData("descriptor/entities-computed-and-rollups.md", 12)]
    [InlineData("descriptor/entities-indexes.md", 15)]
    [InlineData("descriptor/automation.md", 16)]
    public void Pages_follow_the_schema_order_with_the_entities_split_in_place(string path, int order) =>
        Content(path).ShouldContain($"\n  order: {order}\n");

    private IEnumerable<string> Blocks() => _walker.TopLevelKeys.Where(_walker.IsBlock);

    private IEnumerable<GeneratedPage> DescriptorPages() => _pages.Where(page => page.RelativePath.StartsWith("descriptor/", StringComparison.Ordinal));

    private string Content(string path) => _pages.Single(page => page.RelativePath == path).Content;

    [GeneratedRegex("<a id=\"([^\"]+)\"></a>")]
    private static partial Regex AnchorId();

    [GeneratedRegex("^### ", RegexOptions.Multiline)]
    private static partial Regex KeySection();
}
