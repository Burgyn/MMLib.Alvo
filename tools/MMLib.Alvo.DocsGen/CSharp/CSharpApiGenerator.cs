using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using System.Reflection;
using System.Text;

namespace MMLib.Alvo.DocsGen.CSharp;

internal sealed class CSharpApiGenerator : IPageGenerator
{
    internal const string Directory = "csharp";
    internal const string NoHostFacingTypes = "This package exposes no host-facing types beyond its registration; see the guides.";

    private const int IndexOrder = 10;

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        var docs = XmlDocs.Load(ShippedPackages.All.Select(package => package.Assembly));
        var pages = ShippedPackages.All.Select((package, index) => RenderPackage(package.Package, package.Assembly, docs, IndexOrder + 1 + index));
        return Task.FromResult<IReadOnlyList<GeneratedPage>>([RenderIndex(ShippedPackages.All), .. pages]);
    }

    internal static string DescriptionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException($"{assembly.GetName().Name} has no Description in its project file.");

    internal static GeneratedPage RenderIndex(IReadOnlyList<(string Package, Assembly Assembly)> packages)
    {
        var page = new StringBuilder(Md.Frontmatter(
                "C# API",
                "The host-facing C# surface of each MMLib.Alvo.* package: registration, options and the ports a host calls or implements.",
                IndexOrder,
                "Overview"))
            .Append("Each page lists what a host author touches: the extension methods on `IServiceCollection`, `IAlvoBuilder` and ")
            .Append("`IEndpointRouteBuilder` that register and map Alvo, the options classes they configure, and the ports a host calls or ")
            .Append("implements. Other public types are implementation surface and are not listed. ")
            .Append("Alvo is pre-v0.1: no package is published to NuGet yet.\n\n")
            .Append("For walkthroughs, see [Embed in ASP.NET Core](").Append(SiteLinks.Page("start-here/embed"))
            .Append("), [Call Alvo from your endpoints](").Append(SiteLinks.Page("guides/call-from-endpoints"))
            .Append(") and [Custom CEL functions](").Append(SiteLinks.Page("guides/custom-cel-functions")).Append(").\n\n")
            .Append("| Package | Description |\n|---|---|\n");
        foreach (var (package, assembly) in packages)
        {
            page.Append("| [").Append(Md.Cell(package)).Append("](").Append(SiteLinks.Page($"reference/{PageSlug(package)}")).Append(") | ")
                .Append(Md.Cell(DescriptionOf(assembly))).Append(" |\n");
        }

        return new GeneratedPage(OutputRoot.Reference, $"{Directory}/index.md", page.ToString());
    }

    internal static GeneratedPage RenderPackage(string package, Assembly assembly, XmlDocs docs, int order) =>
        RenderPackage(package, DescriptionOf(assembly), ApiScope.TypesOf(assembly), docs, order);

    internal static GeneratedPage RenderPackage(string package, string description, IReadOnlyList<Type> types, XmlDocs docs, int order)
    {
        var page = new StringBuilder(Md.Frontmatter(package, description, order)).Append(Md.Text(description)).Append('\n');
        if (types.Count == 0)
        {
            page.Append('\n').Append(NoHostFacingTypes).Append('\n');
        }

        foreach (var group in types.GroupBy(type => type.Namespace ?? string.Empty).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            page.Append("\n## Namespace ").Append(Md.Code(group.Key)).Append('\n');
            foreach (var type in group)
            {
                AppendType(page, type, docs);
            }
        }

        return new GeneratedPage(OutputRoot.Reference, $"{PageSlug(package)}.md", page.ToString());
    }

    private static string PageSlug(string package) => $"{Directory}/{package.ToLowerInvariant().Replace('.', '-')}";

    private static void AppendType(StringBuilder page, Type type, XmlDocs docs)
    {
        page.Append("\n### ").Append(Md.Code(TypeNames.Display(type, null))).Append("\n\n*").Append(KindOf(type)).Append("*\n");
        AppendParagraph(page, docs.Summary(DocId.Of(type)));
        AppendParagraph(page, RemarksFilter.ForReaders(docs.Remarks(DocId.Of(type))));
        foreach (var overloads in ApiScope.MembersOf(type).GroupBy(member => member.Name))
        {
            page.Append("\n#### ").Append(Md.Code(overloads.Key)).Append('\n');
            foreach (var member in overloads)
            {
                AppendMember(page, member, docs);
            }
        }
    }

    private static void AppendMember(StringBuilder page, MemberInfo member, XmlDocs docs)
    {
        var id = ApiScope.DocIdOf(member);
        page.Append("\n```csharp\n").Append(Signatures.Of(member)).Append("\n```\n");
        AppendParagraph(page, docs.Summary(id));
        AppendParagraph(page, RemarksFilter.ForReaders(docs.Remarks(id)));
        AppendParameters(page, member, id, docs);
        if (docs.Returns(id) is { Length: > 0 } returns)
        {
            page.Append("\n**Returns:** ").Append(returns).Append('\n');
        }
    }

    private static void AppendParameters(StringBuilder page, MemberInfo member, string id, XmlDocs docs)
    {
        var documented = (member is MethodBase method ? method.GetParameters() : [])
            .Select(parameter => (parameter.Name!, Text: docs.Param(id, parameter.Name!)))
            .Where(parameter => parameter.Text.Length > 0)
            .ToList();
        if (documented.Count == 0)
        {
            return;
        }

        page.Append('\n');
        foreach (var (name, text) in documented)
        {
            page.Append("- ").Append(Md.Code(name)).Append(" — ").Append(text.Replace("\n\n", " ", StringComparison.Ordinal)).Append('\n');
        }
    }

    private static void AppendParagraph(StringBuilder page, string markdown)
    {
        if (markdown.Length > 0)
        {
            page.Append('\n').Append(markdown).Append('\n');
        }
    }

    private static string KindOf(Type type) =>
        type.IsInterface ? "interface"
        : type.IsEnum ? "enum"
        : type.IsValueType ? "struct"
        : type is { IsAbstract: true, IsSealed: true } ? "static class"
        : type.GetMethod("<Clone>$") is not null ? "record"
        : "class";
}
