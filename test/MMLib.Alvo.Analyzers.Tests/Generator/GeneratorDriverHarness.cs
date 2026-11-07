using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>One additional file the driver is handed, with the item metadata MSBuild would pass for it.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Text">The file's text.</param>
/// <param name="Descriptor">The <c>AlvoDescriptor</c> metadata, or <see langword="null"/> for none.</param>
/// <param name="Namespace">The <c>AlvoNamespace</c> metadata, or <see langword="null"/> for none.</param>
internal sealed record InputFile(string Path, string Text, string? Descriptor = "true", string? Namespace = null);

/// <summary>
/// Runs <see cref="AlvoEntityGenerator"/> through a real <see cref="CSharpGeneratorDriver"/>: in-memory additional files, the
/// item metadata and <c>RootNamespace</c> answered as MSBuild's <c>CompilerVisible…</c> items would, steps tracked.
/// </summary>
internal static partial class GeneratorDriverHarness
{
    internal const string RootNamespace = "Consumer";

    internal static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Latest, DocumentationMode.Diagnose);

    internal static (GeneratorDriver Driver, CSharpCompilation Compilation) Create(params (string Path, string Text, string? Descriptor)[] files) =>
        Create(RootNamespace, [.. files.Select(file => new InputFile(file.Path, file.Text, file.Descriptor))]);

    internal static (GeneratorDriver Driver, CSharpCompilation Compilation) Create(string? rootNamespace, params InputFile[] files)
    {
        var (driver, compilation, _) = CreateWithTexts(rootNamespace, files);

        return (driver, compilation);
    }

    /// <summary><see cref="Create(string?, InputFile[])"/>, plus the additional texts handed over, for a later replacement.</summary>
    internal static (GeneratorDriver Driver, CSharpCompilation Compilation, AdditionalText[] Texts) CreateWithTexts(string? rootNamespace, params InputFile[] files)
    {
        AdditionalText[] texts = [.. files.Select(file => new InMemoryText(file.Path, file.Text))];
        var driver = CSharpGeneratorDriver.Create(
            [new AlvoEntityGenerator().AsSourceGenerator()],
            texts,
            ParseOptions,
            new OptionsProvider(rootNamespace, files),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        return (driver, Compilation(), texts);
    }

    /// <summary>An empty consumer compilation over the running process's own assemblies.</summary>
    internal static CSharpCompilation Compilation(CSharpCompilationOptions? options = null) => CSharpCompilation.Create(
        "Consumer",
        [],
        GeneratorHarness.References,
        options ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    internal static GeneratorDriverRunResult Run(params InputFile[] files) => Run(RootNamespace, files);

    internal static GeneratorDriverRunResult Run(string? rootNamespace, params InputFile[] files)
    {
        var (driver, compilation) = Create(rootNamespace, files);

        return driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
    }

    /// <summary>The generated sources by hint name, ordered, line endings normalised.</summary>
    internal static IReadOnlyList<(string Hint, string Text)> Sources(GeneratorDriverRunResult run) =>
    [
        .. run.Results.Single().GeneratedSources.OrderBy(source => source.HintName, StringComparer.Ordinal)
            .Select(source => (source.HintName, source.SourceText.ToString().ReplaceLineEndings("\n"))),
    ];

    /// <summary>Every generated source in one text, hint by hint, the analyzer's version scrubbed.</summary>
    internal static string Concatenated(GeneratorDriverRunResult run) =>
        Version().Replace(string.Concat(Sources(run).Select(source => $"// ==== {source.Hint} ====\n{source.Text}")), "GeneratedCode(\"MMLib.Alvo.Analyzers\", \"{scrubbed}\")");

    [GeneratedRegex("GeneratedCode\\(\"MMLib\\.Alvo\\.Analyzers\", \"[^\"]*\"\\)")]
    private static partial Regex Version();

    internal sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }

    private sealed class OptionsProvider(string? rootNamespace, IReadOnlyList<InputFile> files) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(rootNamespace is null
            ? []
            : new() { ["build_property.RootNamespace"] = rootNamespace });

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options([]);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            files.FirstOrDefault(file => file.Path == textFile.Path) is { } file ? new Options(Metadata(file)) : new Options([]);

        private static Dictionary<string, string> Metadata(InputFile file)
        {
            var metadata = new Dictionary<string, string>();
            if (file.Descriptor is not null)
            {
                metadata["build_metadata.AdditionalFiles.AlvoDescriptor"] = file.Descriptor;
            }

            if (file.Namespace is not null)
            {
                metadata["build_metadata.AdditionalFiles.AlvoNamespace"] = file.Namespace;
            }

            return metadata;
        }
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
