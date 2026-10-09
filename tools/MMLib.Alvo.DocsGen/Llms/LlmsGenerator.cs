namespace MMLib.Alvo.DocsGen.Llms;

internal sealed class LlmsGenerator : IPageGenerator
{
    private const string SkillPrefix = "alvo-descriptor-";

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
        .. Directory.EnumerateDirectories(Path.Combine(repoRoot, ".claude", "skills"), SkillPrefix + "*")
            .Select(directory => (Path.GetFileName(directory), DescriptionOf(Path.Combine(directory, "SKILL.md")))),
    ];

    private static string DescriptionOf(string skillFile) =>
        File.ReadLines(skillFile).Skip(1).TakeWhile(line => line != "---")
            .FirstOrDefault(line => line.StartsWith("description:", StringComparison.Ordinal)) is { } line
            ? line["description:".Length..].Trim()
            : throw new InvalidOperationException($"'{skillFile}' has no description in its frontmatter.");
}
