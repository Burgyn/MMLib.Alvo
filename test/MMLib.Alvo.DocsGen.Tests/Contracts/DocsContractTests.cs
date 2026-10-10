using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Contracts;

public class DocsContractTests
{
    private static readonly string _root = RepositoryRoot.Find();

    [Fact]
    public void The_descriptor_mount_defaults_to_vehicle_registry()
    {
        var compose = File.ReadAllText(Path.Combine(_root, "docker-compose.yml"));

        compose.ShouldContain("- ${ALVO_DESCRIPTOR:-./examples/vehicle-registry/vehicles.alvo.json}:/alvo/descriptor.json:ro");
        File.Exists(Path.Combine(_root, "examples", "vehicle-registry", "vehicles.alvo.json")).ShouldBeTrue();
    }

    [Fact]
    public void The_editor_mapping_points_at_the_served_schema()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "website", "src", "snippets", "shell", "vscode-settings.json")))!;

        settings["json.schemas"]![0]!["url"]!.GetValue<string>()
            .ShouldBe($"{SiteLinks.SiteUrl}{SiteLinks.BasePath}/{SchemaFileGenerator.PublishedPath}");
    }
}
