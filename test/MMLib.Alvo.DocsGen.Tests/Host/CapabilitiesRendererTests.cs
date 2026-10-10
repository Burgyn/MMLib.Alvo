using MMLib.Alvo.DocsGen.Host;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class CapabilitiesRendererTests
{
    private const string Fixture = """
        {
          "honoured": [ "entities" ],
          "warned": [ { "block": "automation", "consequence": "Nothing runs." } ],
          "refused": [ { "slot": "rollup.where", "consequence": "C.", "fix": "F." } ]
        }
        """;

    private const string Frontmatter = "---\ntitle: \"x\"\n---\n\n";

    [Fact]
    public void The_page_lists_the_three_statuses()
    {
        var page = CapabilitiesRenderer.Render(JsonNode.Parse(Fixture)!);

        page.RelativePath.ShouldBe("capabilities.md");
        page.Content.ShouldContain("## Honoured\n\n- `entities`\n");
        page.Content.ShouldContain("## Declared but not run in this build\n\n- `automation` — Nothing runs.\n");
        page.Content.ShouldContain("## Refused at apply\n\n- `rollup.where` — C. **Fix:** F.\n");
    }

    [Fact]
    public void Descriptor_pages_get_a_status_aside()
    {
        var pages = Pages("automation", "entities", "entities-computed-and-rollups");

        var withAsides = CapabilitiesRenderer.WithAsides(pages, JsonNode.Parse(Fixture)!);

        withAsides[0].Content.ShouldBe(Frontmatter + ":::caution[Not run in this build]\nNothing runs.\n:::\n\nBody.\n");
        withAsides[1].ShouldBe(pages[1]);
        withAsides[2].Content.ShouldBe(Frontmatter + ":::danger[Refused at apply]\n`rollup.where` — C. **Fix:** F.\n:::\n\nBody.\n");
    }

    [Fact]
    public void Several_entries_on_one_page_share_one_aside()
    {
        var capabilities = JsonNode.Parse("""
            { "honoured": [], "refused": [],
              "warned": [ { "block": "entity.storage", "consequence": "A." }, { "block": "entity.realtime", "consequence": "B." } ] }
            """)!;

        var page = CapabilitiesRenderer.WithAsides(Pages("entities"), capabilities)[0];

        page.Content.ShouldBe(Frontmatter + ":::caution[Not run in this build]\n`entity.storage` — A.\n\n`entity.realtime` — B.\n:::\n\nBody.\n");
    }

    [Theory]
    [InlineData("entity.storage", "entities")]
    [InlineData("field.default", "entities-fields")]
    [InlineData("rollup.where", "entities-computed-and-rollups")]
    [InlineData("email.data", "entities-hooks")]
    [InlineData("trigger.event", "automation")]
    [InlineData("auth.providers", "auth")]
    [InlineData("http.call", "entities-hooks")]
    [InlineData("entity.update", "entities-hooks")]
    [InlineData("function", "entities-hooks")]
    [InlineData("JSONata", "entities-hooks")]
    [InlineData("bodyFile", "templates")]
    [InlineData("dynamicEntities", "dynamic-entities")]
    public void Each_slot_maps_to_its_descriptor_page(string slot, string page) =>
        CapabilitiesRenderer.PageOf(slot).ShouldBe($"descriptor/{page}.md");

    [Fact]
    public void An_unmapped_slot_is_refused() =>
        Should.Throw<InvalidOperationException>(() => CapabilitiesRenderer.PageOf("storage.bucket"));

    [Fact]
    public void A_slot_whose_page_does_not_exist_is_refused() =>
        Should.Throw<InvalidOperationException>(() => CapabilitiesRenderer.WithAsides(Pages("entities"), JsonNode.Parse(Fixture)!));

    private static List<GeneratedPage> Pages(params string[] slugs) =>
        [.. slugs.Select(slug => new GeneratedPage(OutputRoot.Reference, $"descriptor/{slug}.md", Frontmatter + "Body.\n"))];
}
