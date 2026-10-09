using MMLib.Alvo.DocsGen.Markdown;
using System.Text;

namespace MMLib.Alvo.DocsGen.Schema;

internal static class SchemaSection
{
    internal static void Append(StringBuilder page, SchemaKey key)
    {
        page.Append(Md.Anchor(key.Anchor)).Append('\n')
            .Append("### ").Append(Md.Code(key.Path)).Append("\n\n");
        if (key.Description.Length > 0)
        {
            page.Append(Md.Text(key.Description)).Append("\n\n");
        }

        page.Append("- **Type:** ").Append(Md.Code(key.Type)).Append('\n')
            .Append("- **Required:** ").Append(key.Required ? "yes" : "no").Append('\n');
        AppendFacts(page, key);
        page.Append('\n');
    }

    private static void AppendFacts(StringBuilder page, SchemaKey key)
    {
        if (key.Values.Count > 0)
        {
            page.Append("- **Values:** ").AppendJoin(", ", key.Values.Select(Md.Code)).Append('\n');
        }

        if (key.Default is { } value)
        {
            page.Append("- **Default:** ").Append(Md.Code(value)).Append('\n');
        }

        if (key.Pattern is { } pattern)
        {
            page.Append("- **Pattern:** ").Append(Md.Code(pattern)).Append('\n');
        }

        foreach (var note in key.Notes)
        {
            page.Append("- ").Append(note).Append('\n');
        }
    }
}
