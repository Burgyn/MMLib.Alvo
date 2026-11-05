using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>
/// Compiles consumer sources against the running process's own assemblies — the real core and abstractions among them —
/// and runs ALVO1001 over them. Offline and deterministic: no reference-assembly download.
/// </summary>
internal static class AnalyzerHarness
{
    internal const string Marker = "// ALVO1001";

    private static readonly Lazy<ImmutableArray<MetadataReference>> _all = new(() => References(withAlvo: true));
    private static readonly Lazy<ImmutableArray<MetadataReference>> _withoutAlvo = new(() => References(withAlvo: false));

    internal static Task<IReadOnlyList<Diagnostic>> Alvo1001Async(params string[] sources) => RunAsync(_all.Value, sources);

    internal static Task<IReadOnlyList<Diagnostic>> Alvo1001WithoutAlvoAsync(string source) => RunAsync(_withoutAlvo.Value, [source]);

    /// <summary>The 1-based lines of <paramref name="source"/> that carry the marker.</summary>
    internal static IReadOnlyList<int> MarkedLines(string source) =>
        [.. source.Split('\n').Select((line, index) => (line, index)).Where(l => l.line.Contains(Marker, StringComparison.Ordinal)).Select(l => l.index + 1)];

    internal static IReadOnlyList<int> Lines(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Select(d => d.Location.GetLineSpan().StartLinePosition.Line + 1).Order()];

    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(ImmutableArray<MetadataReference> references, string[] sources)
    {
        var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: $"Consumer{index}.cs", cancellationToken: TestContext.Current.CancellationToken));
        var compilation = CSharpCompilation.Create(
            "Consumer", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty("the consumer source must compile, or the analyzer is judged on a broken tree");

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new InTransactionIoAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. diagnostics.Where(d => d.Id == "ALVO1001").OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start)];
    }

    private static ImmutableArray<MetadataReference> References(bool withAlvo) =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => withAlvo || !Path.GetFileName(path).StartsWith("MMLib.Alvo", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
    ];
}
