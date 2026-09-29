using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>A <c>SKILL.md</c> split into its frontmatter and body, as the Agent Skills standard lays it out.</summary>
/// <remarks>
/// Deliberately not a YAML parser. D24 limits the frontmatter to one-line <c>name</c> and <c>description</c>, in that
/// order and without quotes, and <c>SkillConformanceTests</c> holds every skill to it. The text arrives LF-normalised.
/// </remarks>
internal static partial class SkillMarkdown
{
    internal static SkillParts Parse(string markdown)
    {
        var match = Frontmatter().Match(markdown);
        if (!match.Success)
        {
            throw new InvalidOperationException("A SKILL.md must open with '---', 'name: …', 'description: …', '---'.");
        }

        return new SkillParts(match.Groups["name"].Value, match.Groups["description"].Value, markdown[match.Length..].TrimStart('\n'));
    }

    [GeneratedRegex(@"\A---\nname: (?<name>[^\n]+)\ndescription: (?<description>[^\n]+)\n---\n", RegexOptions.CultureInvariant)]
    private static partial Regex Frontmatter();
}

/// <summary>A skill's frontmatter values and its body.</summary>
internal sealed record SkillParts(string Name, string Description, string Body);
