using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using MMLib.Alvo.Management;
using System.Text;

namespace MMLib.Alvo.DocsGen.Host;

internal static class ManagementApiRenderer
{
    private static readonly Type[] _interfaces = [typeof(IAlvoManagement), typeof(IAlvoUserAdministration)];

    internal static GeneratedPage Render(IReadOnlyList<ManagementRouteInfo> routes, XmlDocs docs)
    {
        var page = new StringBuilder(Md.Frontmatter("Management API", "Every route of the Management API, read from a running host's route table.", 5));
        AppendIntro(page);
        var rows = routes.Select(route => Describe(route, docs)).ToList();
        foreach (var contract in _interfaces)
        {
            AppendSection(page, contract, [.. rows.Where(row => row.Contract == contract)]);
        }

        return new GeneratedPage(OutputRoot.Reference, "management-api.md", page.ToString());
    }

    private static void AppendIntro(StringBuilder page) =>
        page.Append("The Management API manages the project itself: the descriptor, its revisions, policy simulation, expression checks and, where the deployment has a membership store, the people who sign in. ")
            .Append("Every route below sits under `/management`, the default of `AlvoManagementOptions.RoutePrefix` (`Alvo:Management:RoutePrefix`). ")
            .Append("This page is read from the route table of a real host booted over the vehicle-registry example, and each summary is the XML documentation of the port member the route calls.\n\n")
            .Append("The management surface is deliberately not in the OpenAPI document: it is excluded from the published Data API contract, and a document of its own is a follow-on. ")
            .Append("That is a deviation from the docs-site design, which planned one OpenAPI-rendered page for both APIs.\n\n")
            .Append("Each route requires a management access level (admin, developer or viewer) granted by the descriptor's `access` block. ")
            .Append("[For coding agents](").Append(SiteLinks.Page("start-here/coding-agents"))
            .Append(") explains the levels; the per-route level is not listed here.\n");

    private static void AppendSection(StringBuilder page, Type contract, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        page.Append("\n## ").Append(contract.Name).Append("\n\n| Method | Route | Member | Summary |\n|---|---|---|---|\n");
        foreach (var row in rows.OrderBy(row => row.Route.Route, StringComparer.Ordinal).ThenBy(row => row.Route.Method, StringComparer.Ordinal))
        {
            page.Append("| ").Append(row.Route.Method).Append(" | ").Append(Md.CodeCell(row.Route.Route)).Append(" | ")
                .Append(Md.CodeCell(row.Route.Member)).Append(" | ").Append(Md.MarkdownCell(row.Summary)).Append(" |\n");
        }
    }

    private static Row Describe(ManagementRouteInfo route, XmlDocs docs)
    {
        foreach (var contract in _interfaces)
        {
            if (docs.FirstIdStartingWith($"M:{contract.FullName}.{route.Member}(") is { } id)
            {
                var summary = docs.Summary(id);
                return string.IsNullOrWhiteSpace(summary)
                    ? throw new InvalidOperationException($"'{id}' has no XML summary; the Management API page needs one for {route.Method} {route.Route}.")
                    : new Row(route, contract, summary);
            }
        }

        throw new InvalidOperationException($"{route.Method} {route.Route} calls '{route.Member}', a member of neither {string.Join(" nor ", _interfaces.Select(type => type.Name))}.");
    }

    private sealed record Row(ManagementRouteInfo Route, Type Contract, string Summary);
}
