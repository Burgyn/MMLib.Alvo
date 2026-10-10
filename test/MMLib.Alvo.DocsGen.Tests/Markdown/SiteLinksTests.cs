using MMLib.Alvo.DocsGen.Markdown;

namespace MMLib.Alvo.DocsGen.Tests.Markdown;

public class SiteLinksTests
{
    [Fact]
    public void Page_prefixes_the_base_and_ends_with_a_slash() =>
        SiteLinks.Page("reference/problem-types").ShouldBe("/reference/problem-types/");

    [Fact]
    public void RepoBlob_points_at_main() =>
        SiteLinks.RepoBlob("docs/architecture/cel.md").ShouldBe("https://github.com/Burgyn/MMLib.Alvo/blob/main/docs/architecture/cel.md");

    [Fact]
    public void The_site_and_base_match_astro_config()
    {
        var config = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "website", "astro.config.mjs"));
        config.ShouldContain($"const site = '{SiteLinks.SiteUrl}'");
        config.ShouldContain($"const base = '{SiteLinks.BasePath}/'");
    }
}
