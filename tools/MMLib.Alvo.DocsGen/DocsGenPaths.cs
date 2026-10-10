namespace MMLib.Alvo.DocsGen;

internal sealed record DocsGenPaths(string RepoRoot, string SiteRoot, string ReferenceDir)
{
    private const string SolutionFileName = "MMLib.Alvo.slnx";

    internal string DocsDir => Path.Combine(SiteRoot, "src", "content", "docs");
    internal string GeneratedDir => Path.Combine(SiteRoot, "src", "generated");
    internal string PublicDir => Path.Combine(SiteRoot, "public");

    internal IReadOnlyList<string> OwnedDirectories => [ReferenceDir, GeneratedDir, Path.Combine(PublicDir, "schema")];

    internal IReadOnlyList<string> OwnedFiles =>
    [
        Path.Combine(PublicDir, "llms.txt"),
        Path.Combine(PublicDir, "llms-full.txt"),
        Path.Combine(DocsDir, "examples.md"),
        Path.Combine(DocsDir, "project", "changelog.md"),
        Path.Combine(DocsDir, "project", "contributing.md"),
    ];

    internal string RootOf(OutputRoot root) => root switch
    {
        OutputRoot.Reference => ReferenceDir,
        OutputRoot.Docs => DocsDir,
        OutputRoot.Generated => GeneratedDir,
        OutputRoot.Public => PublicDir,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
    };

    internal static DocsGenPaths Parse(IReadOnlyList<string> args, string workingDirectory, string startDirectory)
    {
        var options = ReadOptions(args);
        var repo = Resolve(workingDirectory, options.GetValueOrDefault("--repo")) ?? FindRepository(startDirectory);
        var site = Resolve(workingDirectory, options.GetValueOrDefault("--site")) ?? Path.Combine(repo, "website");
        var reference = Resolve(workingDirectory, options.GetValueOrDefault("--out"))
            ?? Path.Combine(site, "src", "content", "docs", "reference");
        return new DocsGenPaths(repo, site, reference);
    }

    private static Dictionary<string, string> ReadOptions(IReadOnlyList<string> args)
    {
        string[] known = ["--repo", "--site", "--out"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!known.Contains(args[i], StringComparer.Ordinal) || i + 1 >= args.Count)
            {
                throw new ArgumentException($"Unknown or incomplete argument '{args[i]}'. Use --repo, --site or --out <path>.", nameof(args));
            }

            options[args[i]] = args[i + 1];
        }

        return options;
    }

    private static string? Resolve(string workingDirectory, string? value) =>
        value is null ? null : Path.GetFullPath(Path.Combine(workingDirectory, value));

    private static string FindRepository(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No {SolutionFileName} above '{startDirectory}'; pass --repo <path>.");
    }
}
