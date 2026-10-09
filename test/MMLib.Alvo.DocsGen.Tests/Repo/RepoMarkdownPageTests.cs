using MMLib.Alvo.DocsGen.Repo;

namespace MMLib.Alvo.DocsGen.Tests.Repo;

public class RepoMarkdownPageTests
{
    [Fact]
    public void Relative_repo_links_become_github_urls()
    {
        var page = RepoMarkdownPage.Render(
            "# Changelog\n\nSee [the spec](docs/product/) and [CLA](docs/legal/CLA-INDIVIDUAL.md#sign), [keep](https://keepachangelog.com/), [here](#unreleased), ![logo](assets/alvo-logo.svg).",
            "Changelog", "What changed.", "CHANGELOG.md");

        page.ShouldContain("[the spec](https://github.com/Burgyn/MMLib.Alvo/blob/main/docs/product/)");
        page.ShouldContain("[CLA](https://github.com/Burgyn/MMLib.Alvo/blob/main/docs/legal/CLA-INDIVIDUAL.md#sign)");
        page.ShouldContain("[keep](https://keepachangelog.com/)");
        page.ShouldContain("[here](#unreleased)");
        page.ShouldContain("![logo](https://github.com/Burgyn/MMLib.Alvo/blob/main/assets/alvo-logo.svg?raw=true)");
        page.ShouldNotContain("# Changelog\n");
    }

    [Fact]
    public void The_source_is_named_in_the_frontmatter_and_footer()
    {
        var page = RepoMarkdownPage.Render("# X\n\nBody.", "Changelog", "What changed.", "CHANGELOG.md");

        page.ShouldStartWith("---\ntitle: \"Changelog\"\ndescription: \"What changed.\"\n---\n\n");
        page.ShouldEndWith("*This page is generated from [`CHANGELOG.md`](https://github.com/Burgyn/MMLib.Alvo/blob/main/CHANGELOG.md).*\n");
    }

    [Fact]
    public void Links_inside_code_are_left_alone()
    {
        var page = RepoMarkdownPage.Render("# X\n\nUse `[a](b)` and\n\n```md\n[c](d)\n```\n", "X", "Y.", "X.md");

        page.ShouldContain("`[a](b)`");
        page.ShouldContain("[c](d)");
    }

    [Fact]
    public void The_real_changelog_and_contributing_render_without_relative_links()
    {
        var root = RepositoryRoot.Find();
        foreach (var file in (string[])["CHANGELOG.md", "CONTRIBUTING.md"])
        {
            var page = RepoMarkdownPage.Render(File.ReadAllText(Path.Combine(root, file)), file, "D.", file);

            page.ShouldNotContain("](docs/");
            page.ShouldNotContain("](CLAUDE.md)");
        }
    }
}
