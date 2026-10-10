using MMLib.Alvo.DocsGen.Exchanges;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Tests.Readme;

public partial class ReadmeTests
{
    private const string SiteUrl = "https://burgyn.github.io/MMLib.Alvo/";
    private static readonly string _root = RepositoryRoot.Find();
    private static readonly string _readme = Read("README.md");

    [Fact]
    public void The_readme_stays_short() => _readme.Split('\n').Length.ShouldBeLessThanOrEqualTo(250);

    [Theory]
    [InlineData("README.md")]
    [InlineData("PACKAGE_README.md")]
    public void The_see_it_excerpt_is_the_example_it_names(string file)
    {
        var match = Excerpt().Match(Read(file));
        match.Success.ShouldBeTrue($"{file} needs '[//]: # (excerpt: <file>#<json pointer>)' followed by a ```json block");

        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, match.Groups["file"].Value)))!;
        var expected = Pointer(source, match.Groups["pointer"].Value);

        JsonNode.DeepEquals(JsonNode.Parse(match.Groups["json"].Value), expected).ShouldBeTrue();
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("PACKAGE_README.md")]
    public void The_see_it_exchange_matches_its_spec(string file)
    {
        var block = ExchangeBlock().Match(Read(file));
        block.Success.ShouldBeTrue($"{file} needs '[//]: # (exchange: readme/see-it)' followed by a ```http block");
        var http = block.Groups["http"].Value;
        var spec = ExchangeSpec.Parse("readme/see-it", File.ReadAllText(Path.Combine(_root, "website", "src", "exchanges", "readme", "see-it.exchange.json")));

        var requests = RequestLine().Matches(http).Select(m => (Method: m.Groups["method"].Value, Path: m.Groups["path"].Value)).ToList();
        var statuses = StatusLine().Matches(http).Select(m => int.Parse(m.Groups["status"].Value, CultureInfo.InvariantCulture)).ToList();

        requests.Select(r => r.Method).ShouldBe(spec.Steps.Select(s => s.Method));
        requests.Zip(spec.Steps).ShouldAllBe(pair => PathPattern(pair.Second.Path).IsMatch(pair.First.Path));
        statuses.ShouldBe(spec.Steps.Select(s => s.Expect));
        http.ShouldContain("X-Alvo-Api-Key: agent.$ALVO_AGENT_KEY_SECRET");
        http.ShouldContain("Content-Type: application/json");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("PACKAGE_README.md")]
    public void The_quick_start_is_the_shared_snippet(string file)
    {
        var snippet = Read("website/src/snippets/shell/readme-quick-start.sh").Trim();

        Read(file).ShouldContain("```bash\n" + snippet + "\n```");
    }

    [Fact]
    public void The_quick_start_starts_the_stack_the_way_the_quick_start_page_does()
    {
        var page = Read("website/src/snippets/shell/quick-start.sh").Split('\n');
        var readme = Read("website/src/snippets/shell/readme-quick-start.sh").Trim().Split('\n');

        readme.Take(3).ShouldBe(page.Take(3));
        readme.Length.ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Every_feature_links_its_guide_and_says_planned_not_a_phase()
    {
        var features = Section(_readme, "## What you get");
        var bullets = features.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToList();

        bullets.Count.ShouldBeInRange(6, 8);
        bullets.ShouldAllBe(line => line.Contains("](" + SiteUrl, StringComparison.Ordinal));
        features.ShouldNotMatch(@"\bF\d\b");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("PACKAGE_README.md")]
    public void Every_site_link_names_a_real_page(string file)
    {
        var paths = SiteLink().Matches(Read(file)).Select(m => m.Groups["path"].Value).Distinct().ToList();

        paths.ShouldNotBeEmpty();
        paths.Where(path => !IsSitePage(path)).ShouldBeEmpty();
    }

    [Fact]
    public void The_packages_table_matches_each_csproj_description()
    {
        foreach (var (package, description) in PackableDescriptions())
        {
            _readme.ShouldContain($"| `{package}` | {description} |");
        }
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(_root, relative)).ReplaceLineEndings("\n");

    private static JsonNode? Pointer(JsonNode node, string pointer) =>
        pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
            .Aggregate<string, JsonNode?>(node, (current, segment) => current is JsonArray array
                ? array[int.Parse(segment, CultureInfo.InvariantCulture)]
                : current![segment]);

    private static Regex PathPattern(string template) =>
        new("^" + Placeholder().Replace(Regex.Escape(template).Replace(@"\{", "{", StringComparison.Ordinal), "[^/?]+") + "$");

    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading + "\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"README needs a '{heading}' section");
        var end = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);

        return end < 0 ? text[start..] : text[start..end];
    }

    private static bool IsSitePage(string path)
    {
        if (path.Length == 0 || path == "llms.txt" || path.StartsWith("reference/", StringComparison.Ordinal))
        {
            return true;
        }

        var page = Path.Combine(_root, "website", "src", "content", "docs", path.TrimEnd('/'));
        return path.EndsWith('/') && (File.Exists(page + ".md") || File.Exists(page + ".mdx"));
    }

    private static List<(string Package, string Description)> PackableDescriptions() =>
        Directory.GetFiles(Path.Combine(_root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Project: XDocument.Load(path)))
            .Where(csproj => csproj.Project.Descendants("IsPackable").All(e => e.Value != "false"))
            .Select(csproj => (csproj.Name, csproj.Project.Descendants("Description").Single().Value))
            .ToList();

    [GeneratedRegex(@"\[//\]: # \(excerpt: (?<file>[^#\s]+)#(?<pointer>[^)\s]+)\)\s*```json\n(?<json>.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex Excerpt();

    [GeneratedRegex(@"\[//\]: # \(exchange: readme/see-it\)\s*```http\n(?<http>.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex ExchangeBlock();

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|PATCH|DELETE) (?<path>\S+) HTTP/1\.1$", RegexOptions.Multiline)]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"^HTTP/1\.1 (?<status>\d{3})\b", RegexOptions.Multiline)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"\{[^}/]+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"https://burgyn\.github\.io/MMLib\.Alvo/(?<path>[^\s)""'#]*)")]
    private static partial Regex SiteLink();
}
