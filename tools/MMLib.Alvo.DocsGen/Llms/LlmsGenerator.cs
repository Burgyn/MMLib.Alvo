namespace MMLib.Alvo.DocsGen.Llms;

internal sealed class LlmsGenerator : IPageGenerator
{
    private const string SkillPrefix = "alvo-descriptor-";
    private const string DescriptionKey = "description:";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var pages = Pages(context.Paths);
        var skills = Skills(context.Paths.RepoRoot);
        return Task.FromResult<IReadOnlyList<GeneratedPage>>(
        [
            new GeneratedPage(OutputRoot.Public, "llms.txt", LlmsWriter.Index(pages, skills)),
            new GeneratedPage(OutputRoot.Public, "llms-full.txt", LlmsWriter.Full(pages)),
        ]);
    }

    internal static IReadOnlyList<ContentPage> Pages(DocsGenPaths paths) =>
    [
        .. Directory.EnumerateFiles(paths.DocsDir, "*.*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".md" or ".mdx")
            .Where(file => Path.GetRelativePath(paths.DocsDir, file) != "index.mdx")
            .Order(StringComparer.Ordinal)
            .Select(file => ContentPage.Read(file, paths.DocsDir, paths.GeneratedDir)),
    ];

    internal static IReadOnlyList<(string Name, string Description)> Skills(string repoRoot) =>
    [
        .. Directory.EnumerateDirectories(Path.Combine(repoRoot, "plugins", "alvo", "skills"), SkillPrefix + "*")
            .Select(directory => (Path.GetFileName(directory), DescriptionOf(Path.Combine(directory, "SKILL.md")))),
    ];

    internal static string DescriptionOf(string skillFile)
    {
        var front = File.ReadLines(skillFile).Skip(1).TakeWhile(line => line != "---").ToList();
        var start = front.FindIndex(line => line.StartsWith(DescriptionKey, StringComparison.Ordinal));
        var value = start < 0 ? string.Empty : front[start][DescriptionKey.Length..].Trim();
        if (value.Length > 0 && value[0] is '>' or '|')
        {
            value = string.Join(' ', front.Skip(start + 1).TakeWhile(line => line.StartsWith(' ') || line.Length == 0).Select(line => line.Trim()).Where(line => line.Length > 0));
        }

        value = value.Trim('"', '\'');
        return value.Length > 0 ? value : throw new InvalidOperationException($"'{skillFile}' has no description in its frontmatter.");
    }
}
