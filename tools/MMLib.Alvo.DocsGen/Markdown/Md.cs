using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Markdown;

internal static partial class Md
{
    internal static string Text(string value)
    {
        var collapsed = Whitespace().Replace(value, " ");
        var builder = new StringBuilder(collapsed.Length);
        for (var index = 0; index < collapsed.Length; index++)
        {
            var spanEnd = collapsed[index] == '`' ? collapsed.IndexOf('`', index + 1) : -1;
            if (spanEnd > index)
            {
                builder.Append(collapsed, index, spanEnd - index + 1);
                index = spanEnd;
                continue;
            }

            builder.Append(Escape(collapsed[index]));
        }

        return builder.ToString();
    }

    internal static string Cell(string value) => Text(value).Replace("|", @"\|", StringComparison.Ordinal);

    internal static string Code(string value)
    {
        var collapsed = Whitespace().Replace(value, " ");
        return collapsed.Contains('`', StringComparison.Ordinal) ? $"`` {collapsed} ``" : $"`{collapsed}`";
    }

    internal static string CodeCell(string value) => Code(value).Replace("|", @"\|", StringComparison.Ordinal);

    internal static string Anchor(string id) => $"<a id=\"{id}\"></a>";

    internal static string Yaml(string value) =>
        "\"" + value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static string Frontmatter(string title, string description, int? order = null, string? sidebarLabel = null, int? tocMaxHeadingLevel = null)
    {
        var builder = new StringBuilder("---\n")
            .Append("title: ").Append(Yaml(title)).Append('\n')
            .Append("description: ").Append(Yaml(Whitespace().Replace(description, " ").Trim())).Append('\n');
        AppendSidebar(builder, order, sidebarLabel);
        if (tocMaxHeadingLevel is { } level)
        {
            builder.Append("tableOfContents:\n  maxHeadingLevel: ").Append(level.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.Append("---\n\n").ToString();
    }

    private static void AppendSidebar(StringBuilder builder, int? order, string? label)
    {
        if (order is null && label is null)
        {
            return;
        }

        builder.Append("sidebar:\n");
        if (order is { } position)
        {
            builder.Append("  order: ").Append(position.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (label is not null)
        {
            builder.Append("  label: ").Append(Yaml(label)).Append('\n');
        }
    }

    private static string Escape(char character) => character switch
    {
        '<' => "&lt;",
        '>' => "&gt;",
        '*' => @"\*",
        '_' => @"\_",
        _ => character.ToString(),
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
