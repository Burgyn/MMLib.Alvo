using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Docs.Tests;

internal static class DocsSnippets
{
    internal const string SnippetPattern = "*.alvo.json";

    internal static string SnippetsDirectory =>
        Path.Combine(RepositoryRoot.Find(), "website", "src", "snippets");

    internal static IEnumerable<string> All() =>
        Directory.Exists(SnippetsDirectory)
            ? Directory.EnumerateFiles(SnippetsDirectory, SnippetPattern, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
            : [];

    internal static IEnumerable<string> Runnable() => All().Where(path => !AlvoExamples.IsMarkedNotRunnable(path));

    internal static IEnumerable<string> NotRunnable() => All().Where(AlvoExamples.IsMarkedNotRunnable);

    internal static string Relative(string path) => Path.GetRelativePath(RepositoryRoot.Find(), path).Replace('\\', '/');
}
