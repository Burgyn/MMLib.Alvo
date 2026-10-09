namespace MMLib.Alvo.DocsGen.Tests;

public class DocsGenRunTests
{
    [Fact]
    public async Task Owned_outputs_are_cleared_before_writing()
    {
        var site = Directory.CreateTempSubdirectory("docsgen-run-").FullName;
        var paths = new DocsGenPaths(RepositoryRoot.Find(), site, Path.Combine(site, "src", "content", "docs", "reference"));
        var stale = Path.Combine(paths.ReferenceDir, "gone.md");
        Directory.CreateDirectory(paths.ReferenceDir);
        await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);

        var exit = await DocsGenRun.RunAsync(paths, [new FixedGenerator(new GeneratedPage(OutputRoot.Reference, "kept.md", "fresh"))], TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        File.Exists(stale).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(paths.ReferenceDir, "kept.md"), TestContext.Current.CancellationToken)).ShouldBe("fresh");
    }

    private sealed class FixedGenerator(params GeneratedPage[] pages) : IPageGenerator
    {
        public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GeneratedPage>>(pages);
    }
}
