using System.Reflection;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Xml;

internal sealed class XmlDocs
{
    private readonly Dictionary<string, XElement> _members;

    private XmlDocs(Dictionary<string, XElement> members) => _members = members;

    internal static XmlDocs Load(IEnumerable<Assembly> assemblies)
    {
        var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var assembly in assemblies.Distinct())
        {
            Index(members, XDocument.Load(DocumentationFileOf(assembly)));
        }

        return new XmlDocs(members);
    }

    internal static XmlDocs Parse(string xml)
    {
        var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        Index(members, XDocument.Parse(xml));
        return new XmlDocs(members);
    }

    internal bool Has(string docId) => _members.ContainsKey(docId);

    internal string? FirstIdStartingWith(string prefix) =>
        _members.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).FirstOrDefault();

    internal string Summary(string docId) => Child(docId, "summary");

    internal string Returns(string docId) => Child(docId, "returns");

    internal string Remarks(string docId) => Child(docId, "remarks");

    internal string Param(string docId, string name) =>
        _members.TryGetValue(docId, out var member)
        && member.Elements("param").FirstOrDefault(param => (string?)param.Attribute("name") == name) is { } element
            ? XmlDocText.ToMarkdown(element)
            : string.Empty;

    private string Child(string docId, string name) =>
        _members.TryGetValue(docId, out var member) && member.Element(name) is { } element
            ? XmlDocText.ToMarkdown(element)
            : string.Empty;

    private static string DocumentationFileOf(Assembly assembly)
    {
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"No XML documentation for {assembly.GetName().Name} at '{path}'. GenerateDocumentationFile must be on for shipped projects.",
                path);
    }

    private static void Index(Dictionary<string, XElement> members, XDocument document)
    {
        foreach (var member in document.Descendants("member"))
        {
            if ((string?)member.Attribute("name") is { } name)
            {
                members[name] = member;
            }
        }
    }
}
