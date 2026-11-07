using MMLib.Alvo.Analyzers.Generator;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>The whole generated output for every shipped example, pinned file by file.</summary>
public sealed class AlvoEntityGeneratorSnapshotTests
{
    public static TheoryData<string> Examples() =>
        ["bike-workshop", "complex-crm", "field-service", "help-desk", "simple-tasks", "vehicle-registry"];

    [Fact]
    public void Every_example_has_a_snapshot_row() =>
        Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "examples"), "*.alvo.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains("_negative", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(Path.GetDirectoryName(path)!)).Order(StringComparer.Ordinal)
            .ShouldBe(Examples().Select(row => row.Data));

    [Theory]
    [MemberData(nameof(Examples))]
    public Task Generates_the_examples(string example)
    {
        var run = GeneratorDriverHarness.Run(Input(example));

        run.Diagnostics.ShouldBeEmpty();

        return Verify(GeneratorDriverHarness.Concatenated(run)).UseParameters(example);
    }

    /// <summary>The example's descriptor, generated into <c>Examples.{Pascal(example)}</c>.</summary>
    internal static InputFile Input(string example)
    {
        var path = Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "examples", example), "*.alvo.json").Single();

        return new InputFile(path, File.ReadAllText(path), Namespace: $"Examples.{CSharpNames.Pascal(example)}");
    }
}
