namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>The rule's help link opens a page that documents it — the link a developer clicks from the warning.</summary>
public sealed class Alvo1001DocsTests
{
    [Fact]
    public void The_help_link_points_at_the_analyzer_rules_page_s_alvo1001_section()
    {
        AlvoDiagnostics.Alvo1001.HelpLinkUri.ShouldBe("https://alvo.burgyn.online/extending/analyzer-rules/#alvo1001");

        var page = File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "website", "src", "content", "docs", "extending", "analyzer-rules.mdx"));
        page.ShouldContain("## ALVO1001", "Starlight slugs a heading from its whole text, so only a heading that is exactly 'ALVO1001' is #alvo1001");
    }
}
