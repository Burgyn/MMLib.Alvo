using MMLib.Alvo.DocsGen.Markdown;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Host;

internal static class CelCatalogRenderer
{
    private const string BuiltIn = "BuiltIn";
    private const string NullableMark = "?";

    private static readonly Dictionary<string, string> _profiles = new(StringComparer.Ordinal)
    {
        ["Condition"] = "a hook `condition`",
        ["Mutate"] = "a before-hook `mutate` value",
    };

    internal static GeneratedPage Render(JsonNode answer)
    {
        var page = new StringBuilder(Md.Frontmatter("CEL functions", "Every built-in CEL function, generated from the engine's catalog.", 1));
        AppendIntro(page);
        page.Append("| Function | Signature | Returns | Profiles | Summary |\n|---|---|---|---|---|\n");
        foreach (var function in BuiltIns(answer))
        {
            page.Append(Row(function));
        }

        return new GeneratedPage(OutputRoot.Reference, "cel-functions.md", page.ToString());
    }

    private static void AppendIntro(StringBuilder page) =>
        page.Append("This page is generated from `GET {management}/projects/{project}/cel/functions` on a host with no `AddCelFunction` registrations, ")
            .Append("so it lists exactly the functions every Alvo host knows. A name with several overloads has one row per overload.\n\n")
            .Append("A `?` after a parameter type means the function receives a null argument; without it, a null argument makes the whole call null and the function is not invoked. ")
            .Append("A `?` after the return type means the call may yield null even when every argument is present.\n\n")
            .Append("**Profiles** are the descriptor slots a call compiles in: ")
            .Append(string.Join(" and ", _profiles.Select(profile => $"`{profile.Key}` ({profile.Value})")))
            .Append(". Rules, computed fields and access levels admit no function call.\n\n")
            .Append("See [CEL in Alvo](").Append(SiteLinks.Page("concepts/cel"))
            .Append(") for the profiles and the language subset, and [Custom CEL functions](").Append(SiteLinks.Page("guides/custom-cel-functions"))
            .Append(") to register your own.\n\n");

    private static IEnumerable<JsonNode> BuiltIns(JsonNode answer) =>
        answer["functions"]!.AsArray()
            .Select(function => function!)
            .Where(function => Text(function, "provenance") == BuiltIn)
            .OrderBy(function => Text(function, "name"), StringComparer.Ordinal);

    private static string Row(JsonNode function)
    {
        var name = Text(function, "name");
        var parameters = function["parameters"]!.AsArray().Select(parameter => Parameter(parameter!));
        var signature = $"{name}({string.Join(", ", parameters)})";
        var returns = Text(function, "result") + Mark(function, "resultMayBeNull");
        return $"| {Md.CodeCell(name)} | {Md.CodeCell(signature)} | {Md.CodeCell(returns)} | {Profiles(function)} | {Md.Cell(Text(function, "summary"))} |\n";
    }

    private static string Parameter(JsonNode parameter) =>
        $"{Text(parameter, "name")}: {Text(parameter, "type")}{Mark(parameter, "acceptsNull")}";

    private static string Mark(JsonNode node, string flag) => node[flag]!.GetValue<bool>() ? NullableMark : string.Empty;

    private static string Profiles(JsonNode function) =>
        string.Join(", ", function["profiles"]!.AsArray().Select(profile => KnownProfile(profile!.GetValue<string>())));

    private static string KnownProfile(string profile) =>
        _profiles.ContainsKey(profile)
            ? profile
            : throw new InvalidOperationException($"The CEL catalog names the profile '{profile}', which this page does not describe; add it to CelCatalogRenderer.");

    private static string Text(JsonNode node, string property) => node[property]!.GetValue<string>();
}
