using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Host;

/// <summary>
/// The capability sentences are the framework's own, written for the dashboard and the assistant, and some name the
/// roadmap phase that will lift a gap ("F7", "an F4 concern"). A phase code means nothing to a reader of the public
/// site, so it becomes "planned"; an issue number stays, because it links somewhere a reader can follow.
/// </summary>
internal static partial class PlannedWork
{
    private const string Planned = "planned";

    internal static string ForReaders(string text)
    {
        var withIssueFirst = IssueThenPhase().Replace(text, match => $"{Planned} (#{match.Groups["issue"].Value})");
        var withLists = PhaseList().Replace(withIssueFirst, RewriteList);
        return PhaseConcern().Replace(withLists, Planned);
    }

    private static string RewriteList(Match match)
    {
        var issues = match.Groups["items"].Value.Split(',', StringSplitOptions.TrimEntries).Where(item => item.StartsWith('#'));
        return $"({string.Join(", ", issues.Prepend(Planned))})";
    }

    [GeneratedRegex(@"#(?<issue>\d+) \(F\d\)")]
    private static partial Regex IssueThenPhase();

    [GeneratedRegex(@"\((?<items>(?=[^)]*\bF\d\b)(?:F\d|#\d+)(?:,\s*(?:F\d|#\d+))*)\)")]
    private static partial Regex PhaseList();

    [GeneratedRegex(@"\ban F\d concern\b")]
    private static partial Regex PhaseConcern();
}
