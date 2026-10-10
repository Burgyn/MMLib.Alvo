using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Docs.Tests;

namespace MMLib.Alvo.Tests.Descriptor;

public class DocsSnippetTests
{
    public static TheoryData<string> Runnable()
    {
        var runnable = DocsSnippets.Runnable().ToList();
        runnable.ShouldNotBeEmpty("website/src/snippets must hold a runnable snippet, or this theory covers nothing");
        return [.. runnable];
    }

    [Theory]
    [MemberData(nameof(Runnable))]
    public void A_docs_snippet_passes_the_validator_apply_runs(string path)
    {
        var result = new DescriptorValidator().Validate(File.ReadAllText(path));

        result.IsValid.ShouldBeTrue($"{DocsSnippets.Relative(path)}: {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(Runnable))]
    public void A_docs_snippet_maps_to_a_schema(string path)
    {
        var model = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(File.ReadAllText(path)));

        model.Entities.ShouldNotBeEmpty($"{DocsSnippets.Relative(path)} mapped to no entities");
    }

    [Fact]
    public void Every_snippet_marked_not_runnable_really_is_refused()
    {
        foreach (var path in DocsSnippets.NotRunnable())
        {
            new DescriptorValidator().Validate(File.ReadAllText(path)).IsValid
                .ShouldBeFalse($"{DocsSnippets.Relative(path)} sits beside NOT-RUNNABLE.md but now applies — delete the marker");
        }
    }

    [Fact]
    public void The_host_function_snippet_is_vehicle_registry_plus_one_hook_block()
    {
        var root = MMLib.Alvo.Testing.RepositoryRoot.Find();
        var snippet = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(DocsSnippets.SnippetsDirectory, "custom-cel-functions", "host-only", "vehicles.alvo.json")))!;
        var example = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "examples", "vehicle-registry", "vehicles.alvo.json")))!;

        snippet["entities"]!["vehicles"]!.AsObject().Remove("hooks").ShouldBeTrue();

        System.Text.Json.Nodes.JsonNode.DeepEquals(snippet, example)
            .ShouldBeTrue("the host-function snippet must stay examples/vehicle-registry plus /entities/vehicles/hooks");
    }

    private static string Describe(DescriptorValidationResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message} {error.FixSuggestion}"));
}
