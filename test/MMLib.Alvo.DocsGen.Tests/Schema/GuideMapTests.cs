using MMLib.Alvo.DocsGen.Schema;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public class GuideMapTests
{
    private const string DescriptorFolder = "descriptor/";

    [Fact]
    public void Every_descriptor_page_has_a_guide_or_is_marked_not_shipped()
    {
        var slugs = SchemaReferenceRenderer.Render(RealSchema.Load())
            .Select(page => page.RelativePath)
            .Where(path => path.StartsWith(DescriptorFolder, StringComparison.Ordinal))
            .Select(path => path[DescriptorFolder.Length..^".md".Length]);

        slugs.Order(StringComparer.Ordinal).ShouldBe(GuideMap.Guides.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_guide_is_a_page()
    {
        var docs = Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs");
        var missing = GuideMap.Guides.Values
            .OfType<string>()
            .Where(guide => !File.Exists(Path.Combine(docs, guide + ".md")) && !File.Exists(Path.Combine(docs, guide + ".mdx")))
            .ToList();

        missing.ShouldBeEmpty();
    }
}
