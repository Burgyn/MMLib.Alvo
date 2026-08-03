using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests;

/// <summary>
/// The shipped <c>src/</c> tree as text, for the facts whose subject is the <b>absence</b> of code rather than
/// the behaviour of any.
/// </summary>
/// <remarks>
/// <para>
/// <b>Comments are stripped, always.</b> A refusal's whole job is to name the feature it refuses, so the
/// prose and the string literals that word a refusal necessarily mention it — and an XML doc saying
/// "JSONata is not evaluated" must never read as an implementation of JSONata. Stripping comments is what
/// keeps an absence fact about code; leaving them in would make every well-documented refusal an offender.
/// </para>
/// <para>
/// <b><c>bin/</c> and <c>obj/</c> are excluded</b> because they carry generated sources and copies of other
/// projects' output, which would make the answer depend on what was last built rather than on what is
/// committed.
/// </para>
/// </remarks>
internal static class ShippedSources
{
    /// <summary>Every committed <c>*.cs</c> file under <c>src/</c>.</summary>
    internal static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .OrderBy(file => file, StringComparer.Ordinal);

    /// <summary>The file names under <c>src/</c> whose <em>code</em> contains <paramref name="text"/>.</summary>
    /// <param name="text">The text to look for, compared ordinally.</param>
    internal static IReadOnlyList<string> FileNamesMentioning(string text) =>
        [.. Files()
            .Where(file => CodeOf(file).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()];

    /// <summary>One file's lines with every comment line removed.</summary>
    /// <param name="file">The source file to read.</param>
    internal static string CodeOf(string file) =>
        string.Join('\n', File.ReadLines(file).Where(IsNotAComment));

    private static bool IsNotAComment(string line) => !line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
