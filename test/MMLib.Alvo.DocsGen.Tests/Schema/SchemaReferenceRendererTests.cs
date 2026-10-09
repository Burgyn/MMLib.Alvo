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

        pages.Select(page => page.RelativePath).ShouldBe(["index.md", "descriptor/index.md", "descriptor/items.md"]);
        pages.ShouldAllBe(page => page.Root == OutputRoot.Reference);
        var index = Page(pages, "descriptor/index.md");
        index.ShouldContain("### `name`");
        index.ShouldContain("/MMLib.Alvo/reference/descriptor/items/");
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

        functions.ShouldContain("**Not in this build:** see [What works today](/MMLib.Alvo/start-here/what-works-today/).");
    }

    private static string Page(IReadOnlyList<GeneratedPage> pages, string path) => pages.Single(page => page.RelativePath == path).Content;
}
