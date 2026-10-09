using MMLib.Alvo.DocsGen.Schema;

namespace MMLib.Alvo.DocsGen;

internal static class DocsGenRun
{
    internal static IReadOnlyList<IPageGenerator> Generators { get; } = [new SchemaPagesGenerator(), new SchemaFileGenerator()];

    internal static async Task<int> RunAsync(DocsGenPaths paths, IReadOnlyList<IPageGenerator> generators, CancellationToken ct)
    {
        ClearOwnedOutputs(paths);
        var context = new DocsGenContext(paths);
        foreach (var generator in generators)
        {
            var pages = await generator.GenerateAsync(context, ct).ConfigureAwait(false);
            await WriteAsync(paths, pages, ct).ConfigureAwait(false);
        }

        return 0;
    }

    private static void ClearOwnedOutputs(DocsGenPaths paths)
    {
        foreach (var directory in paths.OwnedDirectories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in paths.OwnedFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private static async Task WriteAsync(DocsGenPaths paths, IReadOnlyList<GeneratedPage> pages, CancellationToken ct)
    {
        foreach (var page in pages)
        {
            var target = Path.Combine(paths.RootOf(page.Root), page.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, page.Content, ct).ConfigureAwait(false);
        }
    }
}
