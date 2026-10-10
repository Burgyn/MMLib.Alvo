namespace MMLib.Alvo.DocsGen.Tests;

public class DocsGenPathsTests
{
    [Fact]
    public void Resolves_from_any_working_directory()
    {
        var root = RepositoryRoot.Find();
        var fromWebsite = DocsGenPaths.Parse([], workingDirectory: Path.Combine(root, "website"), startDirectory: AppContext.BaseDirectory);

        fromWebsite.RepoRoot.ShouldBe(root);
        fromWebsite.SiteRoot.ShouldBe(Path.Combine(root, "website"));
        fromWebsite.ReferenceDir.ShouldBe(Path.Combine(root, "website", "src", "content", "docs", "reference"));
    }

    [Fact]
    public void A_relative_out_resolves_against_the_working_directory()
    {
        var root = RepositoryRoot.Find();
        var paths = DocsGenPaths.Parse(["--out", "custom/ref"], Path.Combine(root, "website"), AppContext.BaseDirectory);

        paths.ReferenceDir.ShouldBe(Path.Combine(root, "website", "custom", "ref"));
    }

    [Fact]
    public void An_unknown_argument_is_refused() =>
        Should.Throw<ArgumentException>(() => DocsGenPaths.Parse(["--bogus"], ".", AppContext.BaseDirectory))
            .Message.ShouldContain("--bogus");
}
