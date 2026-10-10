using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Markdown;
using MMLib.Alvo.DocsGen.Xml;
using System.Text;

namespace MMLib.Alvo.DocsGen.Configuration;

internal sealed class ConfigurationGenerator : IPageGenerator
{
    private const string TableHeader = "| Key | Type | Default | Description |\n|---|---|---|---|\n";

    public Task<IReadOnlyList<GeneratedPage>> GenerateAsync(DocsGenContext context, CancellationToken ct)
    {
        IReadOnlyList<System.Reflection.Assembly> assemblies = [.. ShippedAssemblies.All, ShippedAssemblies.Host];
        var sections = OptionsCatalog.Read(assemblies, XmlDocs.Load(assemblies));
        return Task.FromResult<IReadOnlyList<GeneratedPage>>([Render(sections, OptionsCatalog.NotBoundFromConfiguration, OutsideOptionsKeys.All)]);
    }

    internal static GeneratedPage Render(
        IReadOnlyList<ConfigurationSection> sections, IReadOnlyDictionary<Type, string> notBound, IReadOnlyList<OutsideKey> outside)
    {
        var page = new StringBuilder(Md.Frontmatter("Configuration keys", "Every Alvo:* option, its type and default, generated from the options types.", 3))
            .Append("Configuration uses the standard .NET options pattern. ")
            .Append("In environment variables, replace `:` with `__` (`Alvo__Api__DefaultPageSize`).\n");
        foreach (var section in sections)
        {
            AppendSection(page, section);
        }

        AppendNotBound(page, notBound);
        AppendOutside(page, outside);
        return new GeneratedPage(OutputRoot.Reference, "configuration.md", page.ToString());
    }

    internal static string Row(string key, string type, string defaultValue, string description) =>
        $"| {Md.CodeCell(key)} | {Md.Cell(type)} | {Md.MarkdownCell(defaultValue)} | {Md.MarkdownCell(description)} |\n";

    private static void AppendSection(StringBuilder page, ConfigurationSection section)
    {
        page.Append("\n## ").Append(Md.Code(section.Name)).Append("\n\n")
            .Append(section.Scope).Append(". Options type: ").Append(Md.Code(section.OptionsType.Name)).Append(".\n\n")
            .Append(TableHeader);
        foreach (var key in section.Keys)
        {
            page.Append(Row(key.Key, key.Type, key.Default, key.Description));
        }
    }

    private static void AppendNotBound(StringBuilder page, IReadOnlyDictionary<Type, string> notBound)
    {
        page.Append("\n## Not bound from configuration\n\n")
            .Append("These public options types are not read from any configuration section:\n\n");
        foreach (var (type, reason) in notBound.OrderBy(entry => entry.Key.Name, StringComparer.Ordinal))
        {
            page.Append("- ").Append(Md.Code(type.Name)).Append(" — ").Append(Md.Text(reason)).Append('\n');
        }
    }

    private static void AppendOutside(StringBuilder page, IReadOnlyList<OutsideKey> outside)
    {
        page.Append("\n## Keys read outside an options type\n\n")
            .Append("These keys are read directly rather than through an options type.\n\n")
            .Append(TableHeader);
        foreach (var key in outside)
        {
            page.Append(Row(key.Key, key.Type, key.Default, Md.Text(key.Description)));
        }
    }
}
