using MMLib.Alvo.DocsGen.Llms;

namespace MMLib.Alvo.DocsGen.Tests.Llms;

public class LlmsWriterTests
{
    private static readonly ContentPage _guide = new("guides/x", "guides", "X guide", "Does X.", 1, "Intro.");

    [Fact]
    public void The_index_follows_the_llms_txt_shape()
    {
        var index = LlmsWriter.Index([_guide], [("alvo-descriptor-hooks", "Use when hooks.")]);

        index.ShouldStartWith("# Alvo\n\n> Describe your backend in one JSON file.");
        index.ShouldContain("## Guides\n\n- [X guide](https://alvo.burgyn.online/guides/x/): Does X.\n");
        index.ShouldContain("## Descriptor schema and skills\n\n- [Descriptor JSON Schema](https://alvo.burgyn.online/schema/v1/project.json)");
        index.ShouldContain("- [alvo-descriptor-hooks](https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/.claude/skills/alvo-descriptor-hooks/SKILL.md): Use when hooks.\n");
        index.ShouldContain("https://alvo.dev/errors/<slug>");
        index.ShouldContain("## Optional\n\n- [Full text](https://alvo.burgyn.online/llms-full.txt)");
        index[index.IndexOf("## Optional", StringComparison.Ordinal)..].ShouldNotContain("project.json");
    }

    [Fact]
    public void Sections_follow_the_sidebar_and_coding_agents_lead_start_here()
    {
        ContentPage[] pages =
        [
            new("project/license", "project", "License", "L.", int.MaxValue, "."),
            new("reference/limits", "reference", "Limits and budgets", "Li.", 3, "."),
            new("start-here/why-alvo", "start-here", "Why Alvo", "W.", 1, "."),
            new("start-here/coding-agents", "start-here", "For coding agents", "A.", 13, "."),
            new("examples", string.Empty, "Examples", "E.", int.MaxValue, "."),
            new("concepts/cel", "concepts", "CEL in Alvo", "C.", int.MaxValue, "."),
            new("data-api/conventions", "data-api", "Data API conventions", "D.", 1, "."),
            _guide,
        ];

        var index = LlmsWriter.Index(pages, []);

        string[] order = ["## Start here", "## Guides", "## Examples", "## Concepts", "## Reference", "## Project", "## Optional"];
        order.Select(heading => index.IndexOf(heading, StringComparison.Ordinal)).ShouldBeInOrder();
        index.IndexOf("[For coding agents]", StringComparison.Ordinal).ShouldBeLessThan(index.IndexOf("[Why Alvo]", StringComparison.Ordinal));
        index.ShouldContain("- [Data API — example (vehicle-registry)](https://alvo.burgyn.online/reference/data-api/): ");
        index.ShouldContain("- [Data API conventions](https://alvo.burgyn.online/data-api/conventions/): D.\n");
    }

    [Fact]
    public void Nested_reference_pages_stay_together_under_their_index()
    {
        ContentPage[] pages =
        [
            new("reference/csharp/mmlib-alvo", "reference", "MMLib.Alvo", "C1.", 1, "."),
            new("reference/descriptor/entities", "reference", "entities", "E.", 2, "."),
            new("reference/csharp", "reference", "C# API", "C.", 10, "."),
            new("reference/descriptor", "reference", "Descriptor", "D.", 1, "."),
            new("reference/limits", "reference", "Limits and budgets", "L.", 5, "."),
        ];

        var index = LlmsWriter.Index(pages, []);

        string[] order = ["[Descriptor]", "[entities]", "[Limits and budgets]", "[C# API]", "[MMLib.Alvo]"];
        order.Select(title => index.IndexOf(title, StringComparison.Ordinal)).ShouldBeInOrder();
    }

    [Fact]
    public void A_page_in_an_unknown_section_fails() =>
        Should.Throw<InvalidOperationException>(() => LlmsWriter.Index([new ContentPage("misc/x", "misc", "X", "D.", 1, ".")], []));

    [Fact]
    public void The_full_text_carries_each_page_with_its_source_url() =>
        LlmsWriter.Full([_guide])
            .ShouldContain("# X guide\n\nSource: https://alvo.burgyn.online/guides/x/\n\nIntro.\n");

    [Fact]
    public void The_full_text_keeps_the_agent_reference_and_leaves_the_rest_to_the_index()
    {
        ContentPage[] pages =
        [
            new("reference/problem-types", "reference", "Problem types", "P.", 2, "P body."),
            new("reference/descriptor/entities", "reference", "entities", "E.", 1, "E body."),
            new("reference/management-api", "reference", "Management API", "M.", 5, "M body."),
            new("project/license", "project", "License", "L.", 1, "L body."),
            _guide,
        ];

        var full = LlmsWriter.Full(pages);

        full.ShouldContain("# Problem types\n");
        full.ShouldContain("# entities\n");
        full.ShouldNotContain("# Management API\n");
        full.ShouldNotContain("# License\n");
        full.IndexOf("# X guide", StringComparison.Ordinal).ShouldBeLessThan(full.IndexOf("# entities", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_without_a_body_is_left_out_of_the_full_text() =>
        LlmsWriter.Full([new ContentPage("guides/empty", "guides", "Empty", "E.", 1, string.Empty), _guide])
            .ShouldNotContain("# Empty\n");

    [Theory]
    [InlineData("description: Plain text.\n", "Plain text.")]
    [InlineData("description: \"Quoted text.\"\n", "Quoted text.")]
    [InlineData("description: >\n  Folded\n  text.\n", "Folded text.")]
    [InlineData("description: |\n  Literal\n  text.\n", "Literal text.")]
    public void A_skill_description_is_read_from_plain_quoted_or_folded_yaml(string yaml, string expected)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("skill-").FullName, "SKILL.md");
        File.WriteAllText(file, "---\nname: s\n" + yaml + "---\n\nBody.\n");

        LlmsGenerator.DescriptionOf(file).ShouldBe(expected);
    }

    [Fact]
    public void A_skill_without_a_description_fails()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("skill-").FullName, "SKILL.md");
        File.WriteAllText(file, "---\nname: s\ndescription: >\n---\n");

        Should.Throw<InvalidOperationException>(() => LlmsGenerator.DescriptionOf(file));
    }
}
