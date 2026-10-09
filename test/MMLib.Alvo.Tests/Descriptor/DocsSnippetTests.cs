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

    private static string Describe(DescriptorValidationResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message} {error.FixSuggestion}"));
}
