using MMLib.Alvo.DocsGen.Host;
using MMLib.Alvo.DocsGen.Xml;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public class HostPagesGeneratorTests
{
    private static readonly HostSnapshot _snapshot = new(
        JsonNode.Parse("""{ "openapi": "3.1.1", "info": { "title": "Alvo", "description": "Generated from the descriptor.", "version": "v1" }, "paths": {} }""")!,
        JsonNode.Parse("""{ "functions": [] }""")!,
        JsonNode.Parse("""{ "honoured": [ "entities" ], "warned": [], "refused": [] }""")!,
        [new("GET", "/management/info", "GetInfoAsync")]);

    private static readonly XmlDocs _docs = XmlDocs.Parse("""
        <doc><members>
          <member name="M:MMLib.Alvo.Management.IAlvoManagement.GetInfoAsync(System.Threading.CancellationToken)"><summary>Describes this instance.</summary></member>
        </members></doc>
        """);

    [Fact]
    public void The_data_api_document_says_it_is_an_example()
    {
        var document = HostPagesGenerator.Render(_snapshot, _docs).Single(page => page.Root == OutputRoot.Generated);

        document.RelativePath.ShouldBe("openapi/data-api.json");
        var description = JsonNode.Parse(document.Content)!["info"]!["description"]!.GetValue<string>();
        description.ShouldStartWith("The Data API Alvo generated for the vehicle-registry example");
        description.ShouldContain("Every descriptor generates its own document at `GET /openapi/v1.json`");
        description.ShouldEndWith("Generated from the descriptor.");
    }

    [Fact]
    public void The_captured_document_is_left_untouched() =>
        _snapshot.OpenApi["info"]!["description"]!.GetValue<string>().ShouldBe("Generated from the descriptor.");

    [Fact]
    public void Every_host_page_is_emitted() =>
        HostPagesGenerator.Render(_snapshot, _docs).Select(page => page.RelativePath)
            .ShouldBe(["cel-functions.md", "management-api.md", "capabilities.md", "openapi/data-api.json"]);

    [Fact]
    public void The_data_api_document_advertises_the_standalone_address()
    {
        var document = JsonNode.Parse(HostPagesGenerator.Render(_snapshot, _docs).Single(page => page.Root == OutputRoot.Generated).Content)!;

        var server = document["servers"]!.AsArray().ShouldHaveSingleItem()!;
        server["url"]!.GetValue<string>().ShouldBe("http://localhost:8080");
        server["description"]!.GetValue<string>().ShouldContain("default");
    }

    [Fact]
    public void Every_link_in_the_description_names_an_existing_page()
    {
        var docs = Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs");

        Should.NotThrow(() => HostPagesGenerator.RequireLinkTargets(HostPagesGenerator.DescriptionOf(HostPagesGenerator.Render(_snapshot, _docs)), docs));
    }

    [Fact]
    public void A_link_to_a_missing_page_fails_the_run()
    {
        var docs = Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs");

        Should.Throw<InvalidOperationException>(() => HostPagesGenerator.RequireLinkTargets("see [x](/MMLib.Alvo/data-api/nowhere/)", docs))
            .Message.ShouldContain("data-api/nowhere");
    }
}
