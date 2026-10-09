namespace MMLib.Alvo.DocsGen.Tests.Problems;

internal static class ProductSource
{
    private static readonly string _src = Path.Combine(RepositoryRoot.Find(), "src");
    private static readonly Lazy<string> _all = new(() => string.Join('\n', Files().Select(Code)));

    internal static string All => _all.Value;

    internal static string Code(string path) =>
        string.Join('\n', File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    internal static string Read(params string[] relative) => Code(Path.Combine([_src, .. relative]));

    internal static bool Emits(string source, string code) => source.Contains($"\"{code}\"", StringComparison.Ordinal);

    private static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(_src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains("MMLib.Alvo.Testing", StringComparison.Ordinal));
}
