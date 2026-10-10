using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The descriptor skills as they sit in the repository: the directories Claude Code discovers and the package embeds
/// (D33). The tests run over these, not over a copy.
/// </summary>
/// <remarks>
/// <b>Reading is tolerant on purpose.</b> A skill whose frontmatter is malformed, or a directory with no
/// <c>SKILL.md</c> at all, still loads, with the keys it does have (none, for a missing file), so the conformance fact
/// that checks the frontmatter fails and names the skill, rather than every test of the suite failing inside this
/// type's initialiser.
/// </remarks>
internal static partial class SkillCatalogue
{
    internal const string Prefix = "alvo-descriptor-";

    internal static string Root { get; } = Path.Combine(RepositoryRoot.Find(), "plugins", "alvo", "skills");

    internal static IReadOnlyList<SkillOnDisk> All { get; } =
        [.. Directory.GetDirectories(Root, Prefix + "*").Order(StringComparer.Ordinal).Select(Read)];

    /// <summary>The skill of an area, found by its directory, so a skill with a broken frontmatter is still found.</summary>
    internal static SkillOnDisk Named(string area) => Keyed(Prefix + area);

    /// <summary>The skill in the directory <paramref name="key"/>.</summary>
    internal static SkillOnDisk Keyed(string key) => All.Single(skill => skill.Key == key);

    /// <summary>Every skill's directory name: what a theory is keyed by.</summary>
    internal static TheoryData<string> Keys() => [.. All.Select(skill => skill.Key)];

    /// <summary>Every <c>gen:</c> region id any skill declares, each once.</summary>
    internal static IReadOnlySet<string> RegionIds() =>
        All.SelectMany(skill => Regions(skill.Body).Keys).ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> Regions(string body) =>
        Region().Matches(body).ToDictionary(match => match.Groups["id"].Value, match => match.Groups["text"].Value, StringComparer.Ordinal);

    internal static IReadOnlyList<string> Tokens(string text) =>
        [.. Token().Matches(text).Select(match => match.Groups["token"].Value)];

    internal static string Expected(IEnumerable<string> tokens) => string.Join(" ", tokens.Select(token => $"`{token}`"));

    /// <summary>Every worked example of every skill, as (skill directory, example) pairs: the rows both suites' outcome facts run.</summary>
    internal static TheoryData<string, string> Examples()
    {
        var data = new TheoryData<string, string>();
        foreach (var skill in All)
        {
            foreach (var example in InstructionExamples.Parse(skill.Body))
            {
                data.Add(skill.Key, example.Name);
            }
        }

        return data;
    }

    /// <summary>The worked example <paramref name="example"/> of the skill <paramref name="skill"/>.</summary>
    internal static InstructionExample Example(string skill, string example) =>
        InstructionExamples.Parse(Keyed(skill).Body).Single(candidate => candidate.Name == example);

    private static SkillOnDisk Read(string directory)
    {
        var file = Path.Combine(directory, "SKILL.md");
        var text = File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings("\n") : string.Empty;
        var block = FrontmatterBlock().Match(text);
        var lines = FrontmatterLine().Matches(block.Groups["block"].Value);
        var values = lines.GroupBy(line => line.Groups["key"].Value).ToDictionary(group => group.Key, group => group.First().Groups["value"].Value, StringComparer.Ordinal);
        var body = block.Success ? text[block.Length..].TrimStart('\n') : text;

        return new SkillOnDisk(
            directory, values.GetValueOrDefault("name", string.Empty), values.GetValueOrDefault("description", string.Empty),
            body, text, [.. lines.Select(line => line.Groups["key"].Value)]);
    }

    [GeneratedRegex(@"\A---\n(?<block>.*?)\n---\n", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterBlock();

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z-]*):[ ]?(?<value>[^\n]*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontmatterLine();

    [GeneratedRegex(@"<!-- gen:(?<id>[a-z-]+) -->\n(?<text>.*?)\n<!-- /gen:\k<id> -->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Region();

    [GeneratedRegex("`(?<token>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

/// <summary>One skill directory: its frontmatter, its body and its whole text, LF-normalised.</summary>
internal sealed record SkillOnDisk(
    string Directory, string Name, string Description, string Body, string Text, IReadOnlyList<string> FrontmatterKeys)
{
    /// <summary>The directory's own name, which the standard requires the frontmatter <see cref="Name"/> to equal.</summary>
    internal string Key => Path.GetFileName(Directory);
}
