using System.Reflection;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.CSharp;

internal static partial class RemarksFilter
{
    private const string InternalSegment = ".Internal";

    private static readonly Lazy<IReadOnlySet<string>> _shippedInternalTypeNames =
        new(() => InternalTypeNames(ShippedAssemblies.All));

    internal static string ForReaders(string remarks) => ForReaders(remarks, _shippedInternalTypeNames.Value);

    internal static string ForReaders(string remarks, IReadOnlySet<string> internalTypeNames) =>
        string.Join("\n\n", Paragraphs(remarks).Where(paragraph => !CitesContributorMaterial(paragraph, internalTypeNames)));

    internal static IReadOnlySet<string> InternalTypeNames(IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(LoadableTypes)
            .Where(type => type.Namespace is { } ns && (ns.EndsWith(InternalSegment, StringComparison.Ordinal) || ns.Contains(InternalSegment + ".", StringComparison.Ordinal)))
            .Select(type => type.Name.Split('`')[0])
            .Where(name => !name.StartsWith('<'))
            .ToHashSet(StringComparer.Ordinal);

    private static bool CitesContributorMaterial(string paragraph, IReadOnlySet<string> internalTypeNames) =>
        ContributorReference().IsMatch(paragraph)
        || CodeSpan().Matches(paragraph).Any(span => Identifier().Matches(span.Groups["code"].Value).Any(name => internalTypeNames.Contains(name.Value)));

    private static List<string> Paragraphs(string markdown)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        var inFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            inFence ^= line.StartsWith("```", StringComparison.Ordinal);
            if (!inFence && line.Trim().Length == 0)
            {
                Flush(paragraphs, current);
                continue;
            }

            current.Add(line);
        }

        Flush(paragraphs, current);
        return paragraphs;
    }

    private static void Flush(List<string> paragraphs, List<string> current)
    {
        if (current.Count > 0)
        {
            paragraphs.Add(string.Join('\n', current));
            current.Clear();
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    [GeneratedRegex(@"(?<!\bRFC \d+ )§|(?<![&\w])#\d+\b|\bPRs?\b|\bRulings?\b|[\w./-]+\.md\b")]
    private static partial Regex ContributorReference();

    [GeneratedRegex("`(?<code>[^`]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex Identifier();
}
