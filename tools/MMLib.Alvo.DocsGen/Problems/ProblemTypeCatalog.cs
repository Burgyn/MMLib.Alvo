using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Xml;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Problems;

internal sealed record ProblemType(string Slug, string Uri, int? Status, string Meaning, string? Statuses = null)
{
    internal string StatusLabel => Statuses ?? Status?.ToString(CultureInfo.InvariantCulture) ?? "—";
}

internal static partial class ProblemTypeCatalog
{
    internal static IReadOnlyList<ProblemType> Read(XmlDocs docs)
    {
        var fields = typeof(AlvoProblemTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);
        return [.. AlvoProblemTypes.All.Select(slug => Describe(slug, fields.TryGetValue(slug, out var field) ? docs.Summary(DocId.Of(field)) : string.Empty))];
    }

    private static ProblemType Describe(string slug, string summary)
    {
        var uri = AlvoProblemTypes.UriOf(slug);
        var match = StatusInSummary().Match(summary);
        if (!match.Success)
        {
            return new ProblemType(slug, uri, null, summary);
        }

        var statuses = match.Groups["statuses"].Value;
        var first = int.Parse(statuses[..3], CultureInfo.InvariantCulture);
        var meaning = summary.Remove(match.Index, match.Length);
        return new ProblemType(slug, uri, first, meaning, statuses.Length > 3 ? statuses : null);
    }

    [GeneratedRegex(@"\s*\((?<statuses>\d{3}(?:(?:, | or )\d{3})*)\)")]
    private static partial Regex StatusInSummary();
}
