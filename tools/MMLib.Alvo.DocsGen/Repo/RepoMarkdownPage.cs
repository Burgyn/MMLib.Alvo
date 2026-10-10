using MMLib.Alvo.DocsGen.Markdown;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Repo;

internal static partial class RepoMarkdownPage
{
    private static readonly string[] _absolutePrefixes = ["http:", "https:", "mailto:", "#"];

    internal static string Render(string markdown, string title, string description, string sourcePath)
    {
        var body = RewriteLinks(WithoutTitle(markdown.Replace("\r\n", "\n", StringComparison.Ordinal)));
        return new StringBuilder(Md.Frontmatter(title, description, editSource: sourcePath))
            .Append(body.Trim())
            .Append("\n\n---\n\n*This page is generated from [`").Append(sourcePath).Append("`](").Append(SiteLinks.RepoBlob(sourcePath)).Append(").*\n")
            .ToString();
    }

    private static string WithoutTitle(string markdown)
    {
        var lines = markdown.Split('\n').ToList();
        var title = lines.FindIndex(line => line.StartsWith("# ", StringComparison.Ordinal));
        if (title >= 0)
        {
            lines.RemoveAt(title);
        }

        return string.Join('\n', lines);
    }

    private static string RewriteLinks(string markdown)
    {
        var inFence = false;
        var lines = markdown.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (IsFence(lines[index]))
            {
                inFence = !inFence;
            }
            else if (!inFence)
            {
                lines[index] = RewriteOutsideCodeSpans(lines[index]);
            }
        }

        return string.Join('\n', lines);
    }

    private static bool IsFence(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string RewriteOutsideCodeSpans(string line)
    {
        var spans = CodeSpan().Matches(line).Select(span => (span.Index, End: span.Index + span.Length)).ToList();
        return Link().Replace(line, match => spans.Any(span => match.Index > span.Index && match.Index < span.End) ? match.Value : Rewrite(match));
    }

    private static string Rewrite(Match match)
    {
        var image = match.Groups["bang"].Value.Length > 0;
        var label = Link().Replace(match.Groups["label"].Value, Rewrite);
        return $"{match.Groups["bang"].Value}[{label}]({Target(match.Groups["target"].Value, image)}{match.Groups["title"].Value})";
    }

    private static string Target(string target, bool image)
    {
        if (_absolutePrefixes.Any(prefix => target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return target;
        }

        if (!image)
        {
            return SiteLinks.RepoBlob(target);
        }

        var hash = target.IndexOf('#', StringComparison.Ordinal);
        return hash < 0
            ? SiteLinks.RepoBlob(target) + "?raw=true"
            : SiteLinks.RepoBlob(target[..hash]) + "?raw=true" + target[hash..];
    }

    [GeneratedRegex("""(?<bang>!?)\[(?<label>(?:[^\[\]]|\[[^\[\]]*\])*)\]\((?<target>[^)\s]+)(?<title>\s+"[^"]*")?\)""")]
    private static partial Regex Link();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex CodeSpan();
}
