using MMLib.Alvo.DocsGen.Markdown;
using System.Text;

namespace MMLib.Alvo.DocsGen.Llms;

internal static class LlmsWriter
{
    internal const string OneLiner =
        "Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.";

    private const string Overview =
        "Alvo is a .NET-native backend-as-a-service: one JSON descriptor (validated by a JSON Schema) defines entities, CEL access rules compiled to SQL, "
        + "hooks, computed fields and webhooks; it runs as a Docker image or embedded in ASP.NET Core. "
        + "Status: pre-v0.1 — no NuGet package or image is published yet.";

    private const string ProblemTypes =
        "Errors are RFC 9457 problem documents. Their `type` is `https://alvo.dev/errors/<slug>`; that domain does not resolve yet, "
        + "so read `https://burgyn.github.io/MMLib.Alvo/reference/problem-types/#<slug>` instead. Branch on the slug, never on `detail`.";

    private const string RawRepository = "https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main";
    private const string CodingAgentsSlug = "start-here/coding-agents";

    private const string DataApiLine =
        "- [Data API — example (vehicle-registry)](" + SiteLinks.SiteUrl + SiteLinks.BasePath + "/reference/data-api/): "
        + "The generated REST API of the vehicle-registry example; every descriptor generates its own at GET /openapi/v1.json.";

    private static readonly (string Heading, string[] Keys)[] _sections =
    [
        ("Start here", ["start-here"]),
        ("Guides", ["guides"]),
        ("Examples", ["examples"]),
        ("Concepts", ["concepts"]),
        ("Reference", ["reference", "data-api"]),
        ("Project", ["project"]),
    ];

    private static readonly string[] _fullTextSlugs =
    [
        "reference/cel-functions",
        "reference/problem-types",
        "reference/capabilities",
        "reference/configuration",
        "reference/limits",
        "data-api/conventions",
    ];

    private static readonly string[] _fullTextSections = ["start-here", "guides", "examples", "concepts"];

    internal static string Index(IReadOnlyList<ContentPage> pages, IReadOnlyList<(string Name, string Description)> skills)
    {
        var index = new StringBuilder("# Alvo\n\n> ").Append(OneLiner).Append("\n\n")
            .Append(Overview).Append("\n\n").Append(ProblemTypes).Append("\n\n");
        AppendSchemaAndSkills(index, skills);
        foreach (var (heading, listed) in Sectioned(pages))
        {
            if (listed.Count == 0 && heading != "Reference")
            {
                continue;
            }

            index.Append("## ").Append(heading).Append("\n\n");
            listed.ForEach(page => index.Append(Line(page.Title, SiteLinks.Absolute(page.Slug), page.Description)));
            if (heading == "Reference")
            {
                index.Append(DataApiLine).Append('\n');
            }

            index.Append('\n');
        }

        return index.Append("## Optional\n\n")
            .Append(Line("Full text", $"{SiteLinks.SiteUrl}{SiteLinks.BasePath}/llms-full.txt",
                "The start-here, guide, example and concept pages plus the descriptor, CEL, problem-type, capability, configuration and limits reference as one file."))
            .ToString();
    }

    internal static string Full(IReadOnlyList<ContentPage> pages)
    {
        var full = new StringBuilder("# Alvo documentation, full text\n\n> ").Append(OneLiner).Append("\n\n")
            .Append(ProblemTypes).Append("\n\n---\n\n");
        foreach (var page in Sectioned(pages).SelectMany(section => section.Pages).Where(page => page.Body.Length > 0 && InFullText(page)))
        {
            full.Append("# ").Append(page.Title).Append("\n\nSource: ").Append(SiteLinks.Absolute(page.Slug)).Append("\n\n")
                .Append(page.Body).Append("\n\n---\n\n");
        }

        return full.ToString();
    }

    private static void AppendSchemaAndSkills(StringBuilder index, IReadOnlyList<(string Name, string Description)> skills)
    {
        index.Append("## Descriptor schema and skills\n\n")
            .Append(Line("Descriptor JSON Schema", $"{SiteLinks.SiteUrl}{SiteLinks.BasePath}/schema/v1/project.json",
                $"validate every descriptor against it; also at {RawRepository}/schema/project.schema.json"));
        foreach (var (name, description) in skills.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            index.Append(Line(name, $"{RawRepository}/.claude/skills/{name}/SKILL.md", description));
        }

        index.Append('\n');
    }

    private static List<(string Heading, List<ContentPage> Pages)> Sectioned(IReadOnlyList<ContentPage> pages)
    {
        var unknown = pages.Where(page => !_sections.Any(section => section.Keys.Contains(KeyOf(page), StringComparer.Ordinal))).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"No llms.txt section for {string.Join(", ", unknown.Select(page => page.Slug))}; add its section to {nameof(LlmsWriter)}.");
        }

        var orders = pages.ToDictionary(page => page.Slug, page => page.Order, StringComparer.Ordinal);
        return
        [
            .. _sections.Select(section => (section.Heading, pages
                .Where(page => section.Keys.Contains(KeyOf(page), StringComparer.Ordinal))
                .OrderBy(page => page.Slug == CodingAgentsSlug ? 0 : 1)
                .ThenBy(page => orders.GetValueOrDefault(GroupOf(page), page.Order))
                .ThenBy(GroupOf, StringComparer.Ordinal)
                .ThenBy(page => page.Slug == GroupOf(page) ? 0 : 1)
                .ThenBy(page => page.Order)
                .ThenBy(page => page.Title, StringComparer.Ordinal)
                .ToList())),
        ];
    }

    private static string KeyOf(ContentPage page) => page.Section.Length > 0 ? page.Section : page.Slug;

    private static string GroupOf(ContentPage page)
    {
        var segments = page.Slug.Split('/');
        return segments.Length > 2 ? string.Join('/', segments[..2]) : page.Slug;
    }

    private static bool InFullText(ContentPage page) =>
        _fullTextSections.Contains(KeyOf(page), StringComparer.Ordinal)
        || page.Slug == "reference/descriptor"
        || page.Slug.StartsWith("reference/descriptor/", StringComparison.Ordinal)
        || _fullTextSlugs.Contains(page.Slug, StringComparer.Ordinal);

    private static string Line(string title, string url, string description) =>
        description.Length > 0 ? $"- [{title}]({url}): {description}\n" : $"- [{title}]({url})\n";
}
