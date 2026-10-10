using MMLib.Alvo.DocsGen.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public class SchemaReferenceRendererTests
{
    [Fact]
    public void The_block_page_has_the_exact_key_section()
    {
        var items = Page(SchemaReferenceRenderer.Render(SchemaFixture.Load()), "descriptor/items.md");

        items.ShouldContain("""
            <a id="items.kind"></a>
            ### `items.<name>.kind`

            The kind.

            - **Type:** `string`
            - **Required:** yes
            - **Values:** `"a"`, `"b"`
            - **Default:** `"a"`
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Scalars_live_on_the_index_and_blocks_get_pages()
    {
        var pages = SchemaReferenceRenderer.Render(SchemaFixture.Load());

        pages.Select(page => page.RelativePath).ShouldBe(["descriptor/index.md", "descriptor/items.md"]);
        pages.ShouldAllBe(page => page.Root == OutputRoot.Reference);
        var index = Page(pages, "descriptor/index.md");
        index.ShouldContain("### `name`");
        index.ShouldContain("/reference/descriptor/items/");
    }

    [Fact]
    public void Description_text_renders_literally()
    {
        var schema = SchemaFixture.Load();
        schema["properties"]!["name"]!["description"] = "before* <b> a || b {{x}}";

        var index = Page(SchemaReferenceRenderer.Render(schema), "descriptor/index.md");

        index.ShouldContain(@"before\* &lt;b&gt; a || b {{x}}");
    }

    [Fact]
    public void A_page_without_a_shipped_feature_says_so_instead_of_linking_a_guide()
    {
        var schema = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["functions"] = new JsonObject { ["type"] = "object", ["description"] = "Scripts.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
            },
        };

        var functions = Page(SchemaReferenceRenderer.Render(schema), "descriptor/functions.md");

        functions.ShouldContain("**Not in this build:** see [What works today](/start-here/what-works-today/).");
    }

    [Fact]
    public void A_variant_only_requirement_is_qualified_on_the_page() =>
        Page(SchemaReferenceRenderer.Render(SchemaWalkerTests.Parse(SchemaWalkerTests.Alternatives)), "descriptor/action.md")
            .ShouldContain("- **Required:** yes (in its variant)");

    private static string Page(IReadOnlyList<GeneratedPage> pages, string path) => pages.Single(page => page.RelativePath == path).Content;
}
