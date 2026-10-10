using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Tests.Readme;

public partial class PackageReadmeTests
{
    private static readonly string _root = RepositoryRoot.Find();

    [Fact]
    public void Every_package_packs_the_package_readme()
    {
        var props = XDocument.Load(Path.Combine(_root, "Directory.Build.props"));

        props.Descendants("PackageReadmeFile").Single().Value.ShouldBe("PACKAGE_README.md");
        props.Descendants("None").Select(e => (string?)e.Attribute("Include")).ShouldContain("$(MSBuildThisFileDirectory)PACKAGE_README.md");
        File.Exists(Path.Combine(_root, "PACKAGE_README.md")).ShouldBeTrue();
    }

    [Fact]
    public void The_package_readme_is_safe_for_nuget_org()
    {
        var text = File.ReadAllText(Path.Combine(_root, "PACKAGE_README.md"));

        text.ShouldNotContain("```mermaid");
        text.ShouldNotContain("<picture", Case.Insensitive);
        text.ShouldNotContain(".svg", Case.Insensitive);
        text.ShouldNotContain("<!--");
        RawHtml().IsMatch(text).ShouldBeFalse("nuget.org renders no raw HTML: keep the package readme plain Markdown");
        LinkTarget().Matches(text).Select(m => m.Groups["target"].Value)
            .ShouldAllBe(target => target.StartsWith("https://", StringComparison.Ordinal) || target.StartsWith('#'));
        ImgSource().Matches(text).Select(m => m.Groups["src"].Value)
            .ShouldAllBe(src => src.StartsWith("https://", StringComparison.Ordinal));
    }

    [GeneratedRegex("<[A-Za-z/]")]
    private static partial Regex RawHtml();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)")]
    private static partial Regex LinkTarget();

    [GeneratedRegex(@"<img[^>]+src=""(?<src>[^""]+)""")]
    private static partial Regex ImgSource();
}
