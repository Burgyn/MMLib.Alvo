using Microsoft.Agents.AI;
using MMLib.Alvo.Ai.Internal;

using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The descriptor skills against the Agent Skills standard, their size caps (D32), and what they claim.</summary>
public sealed class SkillConformanceTests
{
    private const int MaximumSkillBytes = 6_144;
    private const int MaximumSkillLines = 200;
    /// <summary>
    /// Tighter than spec §7.4 AC 2's 300: every description is in the always-in-context list (AC 3), and the
    /// pre-flight ruling H3 caps it at about 200.
    /// </summary>
    private const int MaximumDescription = 200;
    private const int MaximumResourceBytes = 16_384;
    private const int MaximumSkills = 10;

    /// <summary>The nine areas, in ordinal order (spec §7.3).</summary>
    private static readonly string[] _areas =
    [
        "capabilities-and-limits", "computed-and-rollups", "entities-and-fields", "field-types-and-formats", "hooks", "indexes",
        "project-access", "rules-and-cel", "traits-and-tenancy",
    ];

    /// <summary>
    /// The areas that teach without a worked example of their own: the computed examples stay in the base prompt,
    /// an access change needs an administrator the example cannot assume, and the limits are answered, not proposed.
    /// </summary>
    private static readonly string[] _withoutExamples = ["capabilities-and-limits", "computed-and-rollups", "project-access"];

    private static readonly SearchValues<char> _quotes = SearchValues.Create("'\"");

    private static readonly string[] _frontmatterKeys = ["name", "description"];

    public static TheoryData<string> SkillKeys() => SkillCatalogue.Keys();

    public static TheoryData<string, string> SkillExamples() => SkillCatalogue.Examples();

    [Fact]
    public void The_catalogue_is_exactly_the_descriptor_areas()
    {
        SkillCatalogue.All.Select(skill => skill.Key).ShouldBe(_areas.Select(area => SkillCatalogue.Prefix + area));
        SkillCatalogue.All.Count.ShouldBeLessThanOrEqualTo(MaximumSkills);
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void A_skill_follows_the_standard(string key)
    {
        var skill = SkillCatalogue.Keyed(key);

        skill.FrontmatterKeys.ShouldBe(_frontmatterKeys);
        skill.Name.ShouldBe(key);
        AgentSkillFrontmatter.ValidateName(skill.Name, out var nameReason).ShouldBeTrue(nameReason);
        AgentSkillFrontmatter.ValidateDescription(skill.Description, out var descriptionReason).ShouldBeTrue(descriptionReason);
        skill.Description.ShouldStartWith("Use when ");
        skill.Description.AsSpan().IndexOfAny(_quotes).ShouldBe(-1, "a description carries no quote or apostrophe (H3)");
        SkillMarkdown.Parse(skill.Text).ShouldBe(new SkillParts(skill.Name, skill.Description, skill.Body));
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void A_skill_keeps_its_size_caps(string key)
    {
        var skill = SkillCatalogue.Keyed(key);

        Encoding.UTF8.GetByteCount(skill.Text).ShouldBeLessThanOrEqualTo(MaximumSkillBytes);
        skill.Text.Count(character => character == '\n').ShouldBeLessThanOrEqualTo(MaximumSkillLines);
        skill.Description.Length.ShouldBeLessThanOrEqualTo(MaximumDescription);
        EmbeddedSkills.SchemaPointers(skill.Body)
            .ShouldAllBe(pointer => Encoding.UTF8.GetByteCount(EmbeddedSkills.Slice(pointer)) <= MaximumResourceBytes);
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void A_skill_says_how_to_act_in_the_dashboard_and_in_this_repository(string key)
    {
        var body = SkillCatalogue.Keyed(key).Body;

        body.ShouldContain("In the dashboard:", Case.Sensitive);
        body.ShouldContain("In this repo:", Case.Sensitive);
    }

    [Fact]
    public void The_embedded_catalogue_is_the_directories_on_disk()
    {
        var onDisk = Directory.GetFiles(SkillCatalogue.Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SkillCatalogue.Root, path).Replace('\\', '/'))
            .Where(path => path.StartsWith(SkillCatalogue.Prefix, StringComparison.Ordinal))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(SkillCatalogue.Root, path)).ReplaceLineEndings("\n"), StringComparer.Ordinal);

        EmbeddedSkills.Files.Keys.Order(StringComparer.Ordinal).ShouldBe(onDisk.Keys.Order(StringComparer.Ordinal));
        onDisk.ShouldAllBe(file => EmbeddedSkills.Files[file.Key] == file.Value);
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void Every_json_fence_in_a_skill_belongs_to_a_worked_example(string key)
    {
        var body = SkillCatalogue.Keyed(key).Body;

        AssistantInstructionsTests.JsonFence().Count(body).ShouldBe(2 * InstructionExamples.Parse(body).Count);
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void A_skill_has_worked_examples_exactly_when_it_is_not_exempt(string key) =>
        (InstructionExamples.Parse(SkillCatalogue.Keyed(key).Body).Count > 0)
            .ShouldBe(!_withoutExamples.Contains(key[SkillCatalogue.Prefix.Length..]));

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void A_skill_scripts_no_reply(string key) =>
        SkillCatalogue.Keyed(key).Body.ShouldNotContain("Reply:", Case.Sensitive);

    [Theory]
    [MemberData(nameof(SkillExamples))]
    public void A_skill_example_calls_a_registered_tool_with_exactly_its_parameters(string skill, string example) =>
        AssistantInstructionsTests.SendsExactlyItsToolsParameters(SkillCatalogue.Example(skill, example));

    [Theory]
    [MemberData(nameof(SkillExamples))]
    public void A_skill_example_patches_the_bike_workshop_and_touches_what_it_claims(string skill, string example)
    {
        var worked = SkillCatalogue.Example(skill, example);
        var descriptor = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")));

        var result = JsonPatch.Apply(descriptor, worked.Operations);

        result.Succeeded.ShouldBeTrue(result.Error?.ToString());
        PatchAdmission.Check(worked.Operations).ShouldBeNull();
        result.ChangedPaths.ShouldBe(worked.ChangedPaths);
    }

    [Theory]
    [MemberData(nameof(SkillKeys))]
    public void Every_snake_case_name_in_a_skills_code_is_a_known_name(string key)
    {
        var body = SkillCatalogue.Keyed(key).Body;
        var known = AssistantInstructionsTests.KnownNames(body);

        AssistantInstructionsTests.SnakeCaseInCode(body).ShouldAllBe(token => known.Contains(token));
    }
}
