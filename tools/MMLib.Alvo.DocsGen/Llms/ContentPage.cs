using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Llms;

internal sealed partial record ContentPage(string Slug, string Section, string Title, string Description, int Order, string Body)
{
    private const string IndexSuffix = "/index";

    internal static ContentPage Read(string file, string docsDir, string generatedDir)
    {
        var (front, body) = SplitFrontmatter(File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿'), file);
        var slug = SlugOf(file, docsDir);
        return new ContentPage(
            slug,
            slug.Contains('/', StringComparison.Ordinal) ? slug[..slug.IndexOf('/', StringComparison.Ordinal)] : string.Empty,
            Value(front, "title:") ?? throw new InvalidOperationException($"'{file}' has no title in its frontmatter."),
            Value(front, "description:") ?? string.Empty,
            Value(front, "  order:") is { } order ? int.Parse(order, CultureInfo.InvariantCulture) : int.MaxValue,
            ContentBody.Render(body, Path.GetDirectoryName(file)!, generatedDir));
    }

    private static (IReadOnlyList<string> Front, string Body) SplitFrontmatter(string text, string file)
    {
        var lines = text.Split('\n');
        var end = lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
        return end < 0
            ? throw new InvalidOperationException($"'{file}' has no YAML frontmatter.")
            : (lines[1..end], string.Join('\n', lines[(end + 1)..]));
    }

    private static string SlugOf(string file, string docsDir)
    {
        var relative = Path.GetRelativePath(docsDir, file).Replace(Path.DirectorySeparatorChar, '/');
        var withoutExtension = relative[..relative.LastIndexOf('.')];
        return withoutExtension.EndsWith(IndexSuffix, StringComparison.Ordinal) ? withoutExtension[..^IndexSuffix.Length] : withoutExtension;
    }

    private static string? Value(IReadOnlyList<string> lines, string key) =>
        lines.FirstOrDefault(line => line.StartsWith(key, StringComparison.Ordinal)) is { } line ? Unquote(line[key.Length..].Trim()) : null;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? EscapedCharacter().Replace(value[1..^1], match => match.Groups[1].Value)
            : value;

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex EscapedCharacter();
}
