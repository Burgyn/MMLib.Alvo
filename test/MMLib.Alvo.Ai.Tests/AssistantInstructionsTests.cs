using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The instructions against what they describe: the tools, the schema, and the examples' own claims.</summary>
public sealed partial class AssistantInstructionsTests
{
    private static readonly string _text = AssistantInstructions.Text;

    private static readonly string[] _computedRules =
    [
        "**Arithmetic**", "**Text**", "**Null rule**", "**Type and length**", "**Ternary**", "**Constants**",
        "**Not another computed field**", "**A text constant is joined, never compared**", "**Never**",
    ];

    private static readonly string[] _toolFacts =
    [
        "fix what the violation's `pointer` names", "`move` puts the member last", "`propose_change` needs a `summary`",
    ];

    private static IReadOnlyList<string> Registered { get; } =
        [.. ManagementTools.For(Substitute.For<IAlvoManagement>(), "p").Functions.Select(tool => tool.Name).Order(StringComparer.Ordinal)];

    public static TheoryData<string> ExampleNames() => [.. InstructionExamples.Parse(_text).Select(example => example.Name)];

    [Fact]
    public void The_instructions_start_with_their_version_line() =>
        _text.Split('\n')[0].TrimEnd('\r').ShouldBe(AssistantInstructions.VersionLine);

    [Fact]
    public void The_tools_section_lists_exactly_the_registered_tools() =>
        ToolBullet().Matches(_text).Select(match => match.Groups["name"].Value).Order(StringComparer.Ordinal)
            .ShouldBe(Registered);

    [Fact]
    public void Every_tool_the_instructions_name_is_registered() =>
        ToolName().Matches(_text).Select(match => match.Value).Distinct()
            .ShouldAllBe(name => Registered.Contains(name));

    [Fact]
    public void The_field_types_are_the_schemas_field_types()
    {
        var line = _text.Split('\n').Single(candidate => candidate.StartsWith("- Field types:", StringComparison.Ordinal));
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;

        Backticked().Matches(line).Select(match => match.Groups["token"].Value)
            .ShouldBe(schema["$defs"]!["fieldType"]!["enum"]!.AsArray().Select(type => type!.GetValue<string>()));
    }

    [Fact]
    public void The_computed_section_states_every_rule_it_owes() =>
        _computedRules.ShouldAllBe(rule => _text.Contains(rule, StringComparison.Ordinal));

    [Fact]
    public void The_instructions_state_what_the_tools_enforce() =>
        _toolFacts.ShouldAllBe(fact => _text.Contains(fact, StringComparison.Ordinal));

    [Fact]
    public void There_are_worked_examples_and_each_calls_a_registered_tool()
    {
        var examples = InstructionExamples.Parse(_text);

        examples.ShouldNotBeEmpty();
        examples.ShouldAllBe(example => Registered.Contains(example.Tool));
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void A_worked_example_patches_the_bike_workshop_and_touches_what_it_claims(string name)
    {
        var example = InstructionExamples.Parse(_text).Single(candidate => candidate.Name == name);
        var descriptor = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json")));

        var result = JsonPatch.Apply(descriptor, example.Operations);

        result.Succeeded.ShouldBeTrue(result.Error?.ToString());
        PatchAdmission.Check(example.Operations).ShouldBeNull();
        result.ChangedPaths.ShouldBe(example.ChangedPaths);
    }

    [GeneratedRegex(@"^- `(?<name>[a-z]+_[a-z_]+)` — ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ToolBullet();

    [GeneratedRegex(@"\b(?:get|check|propose|validate|apply|set|update|delete)_[a-z_]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();

    [GeneratedRegex("`(?<token>[a-z]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
