namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class GuideTitles(string docsDirectory)
{
    private const string TitleKey = "title:";

    private static readonly string[] _extensions = [".md", ".mdx"];

    internal string TitleOf(string guideSlug)
    {
        var file = _extensions.Select(extension => Path.Combine(docsDirectory, guideSlug + extension)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"The guide page '{guideSlug}' named by GuideMap does not exist under {docsDirectory}.");
        var line = File.ReadLines(file).Skip(1).TakeWhile(text => text != "---").FirstOrDefault(text => text.StartsWith(TitleKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The guide page '{file}' has no title in its frontmatter.");
        return Unquote(line[TitleKey.Length..].Trim());
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0]
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;
}
