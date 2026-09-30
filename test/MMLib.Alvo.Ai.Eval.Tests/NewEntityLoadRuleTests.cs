using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Testing;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval.Tests;

/// <summary>
/// The base prompt's load rule for a new entity names exactly the skills the eval's grader needs for the rules skill's
/// whole-entity example (D43) — so the instruction and the grade cannot drift apart.
/// </summary>
/// <remarks>
/// Pre-flight ruling B1: the whole-entity example lives in <c>alvo-descriptor-rules-and-cel</c>, not in the base
/// prompt, so the always-in-context budget holds; the load rule stays in the base prompt, where a turn reads it before
/// it loads anything.
/// </remarks>
public sealed partial class NewEntityLoadRuleTests
{
    private const string Lead = "A new entity is one area per thing it declares";

    private static readonly string _instructions = Read("src", "MMLib.Alvo.Ai", "Instructions", "schema-assistant.md");

    private static readonly string _rulesSkill = Read(".claude", "skills", "alvo-descriptor-rules-and-cel", "SKILL.md");

    [Fact]
    public void The_new_entity_load_rule_names_what_the_grader_needs_for_the_whole_entity_example()
    {
        var rule = _instructions.Split("\n\n").Single(paragraph => paragraph.StartsWith(Lead, StringComparison.Ordinal));
        var example = InstructionExamples.Parse(_rulesSkill).Single(candidate => candidate.Name == "new-entity-with-rules");
        var operation = example.Operations.EnumerateArray().Single();

        var needed = SkillsRead.AreasOf(operation.GetProperty("path").GetString()!, JsonNode.Parse(operation.GetProperty("value").GetRawText()))
            .Select(area => "alvo-descriptor-" + area);

        SkillName().Matches(rule).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(needed.Order(StringComparer.Ordinal));
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryRoot.Find(), .. path])).ReplaceLineEndings("\n");

    [GeneratedRegex("alvo-descriptor-[a-z-]*[a-z]", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();
}
