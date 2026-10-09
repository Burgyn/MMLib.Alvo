namespace MMLib.Alvo.DocsGen.Tests;

public class ReferenceIndexGeneratorTests
{
    [Fact]
    public void Lists_the_pages_on_disk()
    {
        var reference = Directory.CreateTempSubdirectory("docsgen-reference-").FullName;
        Directory.CreateDirectory(Path.Combine(reference, "descriptor"));
        File.WriteAllText(Path.Combine(reference, "limits.md"), "---\ntitle: \"Limits and budgets\"\ndescription: \"Every limit.\"\nsidebar:\n  order: 9\n---\n\nBody.\n");
        File.WriteAllText(Path.Combine(reference, "problem-types.md"), "---\ntitle: \"Problem types\"\ndescription: \"Every \\\"type\\\".\"\nsidebar:\n  order: 3\n---\n");
        File.WriteAllText(Path.Combine(reference, "descriptor", "index.md"), "---\ntitle: \"Descriptor reference\"\ndescription: \"Every key.\"\nsidebar:\n  order: 0\n---\n");
        File.WriteAllText(Path.Combine(reference, "descriptor", "entities.md"), "---\ntitle: \"Entities\"\ndescription: \"Not listed.\"\n---\n");
        File.WriteAllText(Path.Combine(reference, "index.md"), "---\ntitle: \"Stale\"\n---\n");

        var pages = ReferenceIndexGenerator.Scan(reference);

        pages.ShouldBe(
        [
            ("descriptor", "Descriptor reference", "Every key."),
            ("problem-types", "Problem types", "Every \"type\"."),
            ("limits", "Limits and budgets", "Every limit."),
        ]);
    }

    [Fact]
    public void Renders_one_link_per_page()
    {
        var page = ReferenceIndexGenerator.Render([("problem-types", "Problem types", "Every type.")]);

        page.RelativePath.ShouldBe("index.md");
        page.Content.ShouldContain("- [Problem types](/MMLib.Alvo/reference/problem-types/) — Every type.\n");
    }
}
