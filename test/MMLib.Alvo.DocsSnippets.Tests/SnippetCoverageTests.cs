using MMLib.Alvo.Testing;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.DocsSnippets.Tests;

/// <summary>
/// The harness compiles what the docs show (spec 2026-10-14 C8, §4.15): every C# snippet under
/// <c>website/src/snippets/extending</c> is a source file of this assembly, so one that does not compile fails the
/// build and with it ring0. These facts keep that from passing vacuously — a broken glob would compile nothing.
/// </summary>
public sealed class SnippetCoverageTests
{
    private static string Extending =>
        Path.Combine(RepositoryRoot.Find(), "website", "src", "snippets", "extending");

    public static TheoryData<string> CodeSnippets() =>
        [.. Directory.EnumerateFiles(Extending, "*.cs", SearchOption.AllDirectories).Select(Relative).Order(StringComparer.Ordinal)];

    [Fact]
    public void There_is_a_code_snippet_to_compile() =>
        Directory.EnumerateFiles(Extending, "*.cs", SearchOption.AllDirectories)
            .ShouldNotBeEmpty("website/src/snippets/extending must hold a C# snippet, or this harness covers nothing");

    [Theory]
    [MemberData(nameof(CodeSnippets))]
    public void Every_code_snippet_is_compiled_into_this_assembly(string snippet) =>
        typeof(SnippetCoverageTests).Assembly.GetTypes().Select(type => type.Name)
            .ShouldContain(
                Path.GetFileNameWithoutExtension(snippet),
                $"{snippet} must declare a type named after its file, and the harness must compile it");

    [Fact]
    public void Every_descriptor_snippet_has_a_name_no_other_page_uses() =>
        Directory.EnumerateFiles(Extending, "*.alvo.json", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ShouldBeEmpty("descriptor snippets are copied flat to the test output, so two pages must not share a name");

    private static string Relative(string path) => Path.GetRelativePath(Extending, path).Replace('\\', '/');
}
