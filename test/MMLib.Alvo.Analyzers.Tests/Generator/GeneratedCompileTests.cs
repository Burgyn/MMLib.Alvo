using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>What the generator emits compiles against the real core — warning-free under a consumer's strictest settings.</summary>
public sealed class GeneratedCompileTests
{
    private static readonly string[] _benign = ["CS8019", "CS1701", "CS1702"];

    private static readonly CSharpCompilationOptions _strict = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: 9999)
        .WithGeneralDiagnosticOption(ReportDiagnostic.Error);

    private const string Handler = """
        using Microsoft.Extensions.DependencyInjection;
        using MMLib.Alvo.Events;
        using Consumer;

        internal static class Wiring
        {
            internal static void Configure(IServiceCollection services) =>
                services.AddAlvo(a => a.Entity<Ticket>(t => t.OnSaved("saved", (AlvoSaved<Ticket> e) =>
                {
                    bool created = e.Old is null;
                    string? before = BODY;
                    _ = created;
                    _ = before;
                })));
        }
        """;

    [Theory]
    [MemberData(nameof(AlvoEntityGeneratorSnapshotTests.Examples), MemberType = typeof(AlvoEntityGeneratorSnapshotTests))]
    public void Every_example_compiles_against_the_real_core(string example) =>
        Problems(GeneratorDriverHarness.Compilation(), AlvoEntityGeneratorSnapshotTests.Input(example))
            .Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(AlvoEntityGeneratorSnapshotTests.Examples), MemberType = typeof(AlvoEntityGeneratorSnapshotTests))]
    public void Generated_code_compiles_warning_free_under_strict_settings(string example) =>
        Problems(GeneratorDriverHarness.Compilation(_strict), AlvoEntityGeneratorSnapshotTests.Input(example)).ShouldBeEmpty();

    [Fact]
    public void Old_is_nullable_on_a_generated_type_without_a_bang() =>
        Problems(Consumer("e.Old?.Title"), new InputFile("help-desk.alvo.json", Descriptors.HelpDesk)).ShouldBeEmpty();

    [Fact]
    public void Dereferencing_Old_without_a_check_is_a_nullable_warning() =>
        Problems(Consumer("e.Old.Title"), new InputFile("help-desk.alvo.json", Descriptors.HelpDesk))
            .Select(d => d.Id).ShouldBe(["CS8602"]);

    private static CSharpCompilation Consumer(string body) => GeneratorDriverHarness.Compilation(_strict)
        .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            Handler.Replace("BODY", body, StringComparison.Ordinal), GeneratorDriverHarness.ParseOptions, cancellationToken: TestContext.Current.CancellationToken));

    private static IReadOnlyList<Diagnostic> Problems(CSharpCompilation compilation, InputFile input)
    {
        var (driver, _) = GeneratorDriverHarness.Create(GeneratorDriverHarness.RootNamespace, input);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, TestContext.Current.CancellationToken);
        generatorDiagnostics.ShouldBeEmpty();

        return [.. output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => !_benign.Contains(d.Id))];
    }
}
