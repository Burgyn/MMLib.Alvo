using MMLib.Alvo.DocsGen.Markdown;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Llms;

internal static partial class ContentBody
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, string> _languages = new(StringComparer.Ordinal)
    {
        [".json"] = "json",
        [".cs"] = "csharp",
        [".http"] = "http",
        [".sh"] = "sh",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
    };

    internal static string Render(string body, string pageDirectory, string generatedDir)
    {
        var imports = RawImports(body, pageDirectory);
        return OutsideFences(body, prose => Prose(prose, imports, generatedDir)).Trim();
    }

    private static string Prose(string prose, Dictionary<string, (string Path, string Content)> imports, string generatedDir)
    {
        var text = AnchorOnlyLine().Replace(WithoutTagsAndComments(WithoutImports(prose)), string.Empty);
        text = AbsoluteSiteLinks(text);
        text = Code().Replace(text, match => Fence(Language(match.Groups["attrs"].Value, imports), Raw(match, imports).Content.TrimEnd()));
        text = SourceExcerpt().Replace(text, match => Fence(Attribute(match.Groups["attrs"].Value, "lang") ?? "csharp", Slice(match, imports)));
        text = JsonExcerpt().Replace(text, match => Fence("json", JsonSubtree(match, imports)));
        text = Exchange().Replace(text, match => ExchangeText.Render(match.Groups["attrs"].Value, generatedDir));
        return BlankLines().Replace(text, "\n\n");
    }

    private static string OutsideFences(string text, Func<string, string> transform)
    {
        var result = new StringBuilder();
        var prose = new StringBuilder();
        var inFence = false;
        foreach (var line in text.Split('\n'))
        {
            var fence = IsFence(line);
            if (!inFence && !fence)
            {
                prose.Append(line).Append('\n');
                continue;
            }

            if (!inFence)
            {
                AppendProse(result, transform(prose.ToString()));
                prose.Clear();
            }

            result.Append(line).Append('\n');
            inFence = !inFence || !fence;
        }

        AppendProse(result, transform(prose.ToString()));
        return result.ToString();
    }

    private static void AppendProse(StringBuilder result, string prose)
    {
        var trimmed = prose.Trim('\n');
        if (trimmed.Length == 0)
        {
            return;
        }

        if (result.Length > 0)
        {
            result.Append('\n');
        }

        result.Append(trimmed).Append("\n\n");
    }

    private static bool IsFence(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static Dictionary<string, (string Path, string Content)> RawImports(string body, string pageDirectory) =>
        RawImport().Matches(body).ToDictionary(
            match => match.Groups["id"].Value,
            match =>
            {
                var path = Path.GetFullPath(Path.Combine(pageDirectory, match.Groups["path"].Value));
                return (path, File.ReadAllText(path).TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal));
            },
            StringComparer.Ordinal);

    private static string WithoutImports(string body) => ImportLine().Replace(body, string.Empty);

    private static (string Path, string Content) Raw(Match match, Dictionary<string, (string Path, string Content)> imports)
    {
        var id = CodeId().Match(match.Groups["attrs"].Value);
        return id.Success && imports.TryGetValue(id.Groups["id"].Value, out var raw)
            ? raw
            : throw new InvalidOperationException($"'{match.Value.Trim()}' names no '?raw' import of this page.");
    }

    private static string Language(string attrs, Dictionary<string, (string Path, string Content)> imports)
    {
        if (Attribute(attrs, "lang") is { } lang)
        {
            return lang;
        }

        var id = CodeId().Match(attrs).Groups["id"].Value;
        return imports.TryGetValue(id, out var raw) ? _languages.GetValueOrDefault(Path.GetExtension(raw.Path), string.Empty) : string.Empty;
    }

    private static string Slice(Match match, Dictionary<string, (string Path, string Content)> imports)
    {
        var lines = Raw(match, imports).Content.Split('\n');
        var from = RequiredAttribute(match, "from");
        var to = RequiredAttribute(match, "to");
        var start = Array.FindIndex(lines, line => line.Contains(from, StringComparison.Ordinal));
        var end = start < 0 ? -1 : Array.FindIndex(lines, start, line => line.Contains(to, StringComparison.Ordinal));
        if (end < 0)
        {
            throw new InvalidOperationException($"SourceExcerpt anchors not found (from \"{from}\", to \"{to}\").");
        }

        return Dedent(lines[start..(end + 1)]);
    }

    private static string Dedent(IReadOnlyList<string> lines)
    {
        var indent = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart(' ')));
    }

    private static string JsonSubtree(Match match, Dictionary<string, (string Path, string Content)> imports)
    {
        var pointer = RequiredAttribute(match, "pointer");
        JsonNode? node = JsonNode.Parse(Raw(match, imports).Content);
        string? key = null;
        foreach (var token in pointer.Length == 0 ? [] : pointer[1..].Split('/').Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
        {
            (node, key) = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(token, out var child) => (child, token),
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < array.Count => (array[i], null),
                _ => throw new InvalidOperationException($"JsonExcerpt: the pointer \"{pointer}\" resolves to nothing (no \"{token}\")."),
            };
        }

        var value = node?.ToJsonString(_indented) ?? "null";
        return key is null ? value : $"{JsonSerializer.Serialize(key, _indented)}: {value}";
    }

    private static string AbsoluteSiteLinks(string text) =>
        text.Replace($"]({SiteLinks.BasePath}/", $"]({SiteLinks.SiteUrl}{SiteLinks.BasePath}/", StringComparison.Ordinal);

    private static string WithoutTagsAndComments(string text) =>
        TagOnlyLine().Replace(Comment().Replace(text, string.Empty), string.Empty);

    internal static string Fence(string lang, string content)
    {
        var longest = Backticks().Matches(content).Select(run => run.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{lang}\n{content}\n{fence}";
    }

    private static string RequiredAttribute(Match match, string name) =>
        Attribute(match.Groups["attrs"].Value, name)
        ?? throw new InvalidOperationException($"'{match.Value.Trim()}' has no {name}=\"…\" attribute.");

    private static string? Attribute(string attrs, string name)
    {
        var match = Regex.Match(attrs, $"(?:^|\\s){Regex.Escape(name)}=\"(?<value>[^\"]*)\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    [GeneratedRegex("""^\s*import\s+(?<id>\w+)\s+from\s+['"](?<path>[^'"]+)\?raw['"];?\s*$""", RegexOptions.Multiline)]
    private static partial Regex RawImport();

    [GeneratedRegex(@"^[ \t]*import\s.*\sfrom\s.*\n?", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    [GeneratedRegex(@"^[ \t]*<Code\s(?<attrs>.*?)/>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Code();

    [GeneratedRegex(@"^[ \t]*<SourceExcerpt\s(?<attrs>.*?)/>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SourceExcerpt();

    [GeneratedRegex(@"^[ \t]*<JsonExcerpt\s(?<attrs>.*?)/>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex JsonExcerpt();

    [GeneratedRegex(@"^[ \t]*<Exchange\s(?<attrs>.*?)/>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Exchange();

    [GeneratedRegex(@"code=\{(?<id>\w+)(?:\.\w+\(\))?\}")]
    private static partial Regex CodeId();

    [GeneratedRegex(@"^[ \t]*(?!<(?:Code|SourceExcerpt|JsonExcerpt|Exchange)\s)</?[A-Z][A-Za-z.]*(\s[^>]*)?/?>[ \t]*\n?", RegexOptions.Multiline)]
    private static partial Regex TagOnlyLine();

    [GeneratedRegex(@"\{/\*.*?\*/\}|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^[ \t]*<a id=""[^""]*""></a>[ \t]*\n?", RegexOptions.Multiline)]
    private static partial Regex AnchorOnlyLine();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex("`+")]
    private static partial Regex Backticks();
}
