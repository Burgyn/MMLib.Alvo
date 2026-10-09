using MMLib.Alvo.DocsGen.Markdown;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Xml;

internal static partial class XmlDocText
{
    private const char CodeLineBreak = '\u0001';

    internal static string ToMarkdown(XElement element)
    {
        var builder = new StringBuilder();
        AppendNodes(builder, element.Nodes());
        var normalized = BlankLines().Replace(SpacesAroundNewlines().Replace(builder.ToString(), "\n"), "\n\n");
        return normalized.Trim().Replace(CodeLineBreak, '\n');
    }

    private static void AppendNodes(StringBuilder builder, IEnumerable<XNode> nodes)
    {
        foreach (var node in nodes)
        {
            AppendNode(builder, node);
        }
    }

    private static void AppendNode(StringBuilder builder, XNode node)
    {
        if (node is XText text)
        {
            builder.Append(Md.Text(text.Value));
        }
        else if (node is XElement element)
        {
            AppendElement(builder, element);
        }
    }

    private static void AppendElement(StringBuilder builder, XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "c":
                builder.Append(Md.Code(element.Value));
                break;
            case "see" or "seealso":
                builder.Append(Reference(element));
                break;
            case "paramref" or "typeparamref":
                builder.Append(Md.Code((string?)element.Attribute("name") ?? string.Empty));
                break;
            case "b" or "strong":
                Wrap(builder, element, "**");
                break;
            case "i" or "em":
                Wrap(builder, element, "*");
                break;
            case "para":
                Block(builder, element);
                break;
            case "code":
                builder.Append("\n\n```csharp").Append(CodeLineBreak).Append(CodeBody(element.Value)).Append(CodeLineBreak).Append("```\n\n");
                break;
            default:
                AppendNodes(builder, element.Nodes());
                break;
        }
    }

    private static void Wrap(StringBuilder builder, XElement element, string marker)
    {
        builder.Append(marker);
        AppendNodes(builder, element.Nodes());
        builder.Append(marker);
    }

    private static void Block(StringBuilder builder, XElement element)
    {
        builder.Append("\n\n");
        AppendNodes(builder, element.Nodes());
        builder.Append("\n\n");
    }

    private static string Reference(XElement element)
    {
        if ((string?)element.Attribute("langword") is { } langword)
        {
            return Md.Code(langword);
        }

        if ((string?)element.Attribute("href") is { } href)
        {
            var label = element.Value.Trim();
            return $"[{Md.Text(label.Length > 0 ? label : href)}]({href})";
        }

        var inner = element.Value.Trim();
        return Md.Code(inner.Length > 0 ? inner : CrefLabel((string?)element.Attribute("cref") ?? string.Empty));
    }

    private static string CrefLabel(string cref)
    {
        var withoutPrefix = cref.Length > 1 && cref[1] == ':' ? cref[2..] : cref;
        var parameters = withoutPrefix.IndexOf('(', StringComparison.Ordinal);
        var member = parameters >= 0 ? withoutPrefix[..parameters] : withoutPrefix;
        var name = member[(member.LastIndexOf('.') + 1)..];
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity >= 0 ? name[..arity] : name;
    }

    private static string CodeBody(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var content = lines.SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse().ToList();
        var indent = content.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join(CodeLineBreak, content.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
    }

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpacesAroundNewlines();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
