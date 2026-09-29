using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MMLib.Alvo.Ai.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

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
        "**Arithmetic**", "**Text**", "**Null rule**", "**Only a bare `has(f)` is a guard**", "**Type and length**",
        "**Never boolean**", "**Ternary**", "**Constants**", "**Not another computed field**",
        "**A text constant is joined, never compared**", "**Never**",
    ];

    private static readonly string[] _toolFacts =
    [
        "Read each violation's `message` and `fix`", "Apply it at the `pointer`", "The `pointer` is authoritative",
        "`move` puts the member last", "`propose_change` needs a `summary`", "spends one of the same three attempts",
        "`stale-revision`", "`severity` is `warning` does not block",
    ];

    /// <summary>
    /// Snake-case names the instructions may use in code without their being tools: illustrative fields of the
    /// Arithmetic and Never-boolean rules, the rate field example (f) suggests, and the name rule's own example.
    /// </summary>
    private static readonly string[] _illustrativeNames = ["net_total", "vat_total", "is_vip", "vat_rate", "customer_audits"];

    private static readonly string[] _pointerMembers = ["path", "from"];

    private static readonly string[] _skillTools = [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName];

    private static IReadOnlyList<AIFunction> Tools { get; } = ManagementTools.For(Substitute.For<IAlvoManagement>(), "p").Functions;

    internal static IReadOnlyList<string> Registered { get; } = [.. Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal)];

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

        Backticked().Matches(line).Select(match => match.Groups["token"].Value)
            .ShouldBe(Schema()["$defs"]!["fieldType"]!["enum"]!.AsArray().Select(type => type!.GetValue<string>()));
    }

    [Fact]
    public void The_name_rule_is_the_schemas_entity_and_field_name_pattern()
    {
        var schema = Schema();

        InstructionClaims.NamePattern(_text).ShouldSatisfyAllConditions(
            stated => stated.ShouldBe(schema["properties"]!["entities"]!["propertyNames"]!["pattern"]!.GetValue<string>()),
            stated => stated.ShouldBe(schema["$defs"]!["entity"]!["properties"]!["fields"]!["propertyNames"]!["pattern"]!.GetValue<string>()));
    }

    [Fact]
    public void The_managed_columns_are_the_ones_the_framework_injects_for_each_trait()
    {
        var stated = InstructionClaims.ManagedColumns(_text);
        var always = AlvoManagedColumns.For(null, audit: false, softDelete: false);

        stated.Keys.ShouldBe(InstructionClaims.TraitLabels);
        stated[InstructionClaims.EveryEntity].ShouldBe(always, ignoreOrder: true);
        stated[InstructionClaims.ScopedEntity].ShouldBe(Beyond(AlvoManagedColumns.For(TenancyMode.Scoped, audit: false, softDelete: false), always), ignoreOrder: true);
        stated[InstructionClaims.AuditedEntity].ShouldBe(AlvoManagedColumns.Audit);
        stated[InstructionClaims.SoftDeletedEntity].ShouldBe(Beyond(AlvoManagedColumns.For(null, audit: false, softDelete: true), always), ignoreOrder: true);
        stated.Values.SelectMany(columns => columns)
            .ShouldBe(AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true), ignoreOrder: true);
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

    [Fact]
    public void Every_json_fence_belongs_to_a_worked_example() =>
        JsonFence().Count(_text).ShouldBe(2 * InstructionExamples.Parse(_text).Count);

    [Fact]
    public void Every_snake_case_name_in_code_is_a_tool_or_a_descriptor_name()
    {
        var known = KnownNames(_text);

        SnakeCaseInCode(_text).ShouldAllBe(name => known.Contains(name));
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void A_worked_example_sends_exactly_its_tools_parameters(string name) =>
        SendsExactlyItsToolsParameters(InstructionExamples.Parse(_text).Single(candidate => candidate.Name == name));

    /// <summary>Holds a worked example's call to a registered tool's parameters: every one it has, and each required one.</summary>
    internal static void SendsExactlyItsToolsParameters(InstructionExample example)
    {
        Registered.ShouldContain(example.Tool);
        var schema = Tools.Single(tool => tool.Name == example.Tool).JsonSchema;

        example.Arguments.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(schema.GetProperty("properties").EnumerateObject().Select(parameter => parameter.Name).Order(StringComparer.Ordinal));
        schema.GetProperty("required").EnumerateArray().Select(parameter => parameter.GetString()!)
            .ShouldAllBe(parameter => example.Arguments.ContainsKey(parameter));
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

    /// <summary>
    /// Every name a tool, the skill tools, the bike-workshop descriptor, the schema, one of <paramref name="text"/>'s
    /// own example patches or the framework's managed columns define — what a snake-case token in the text may
    /// legitimately be.
    /// </summary>
    internal static HashSet<string> KnownNames(string text)
    {
        var root = RepositoryRoot.Find();
        var names = new HashSet<string>(Registered.Concat(_illustrativeNames).Concat(_skillTools), StringComparer.Ordinal);
        names.UnionWith(PropertyNames(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "examples", "bike-workshop", "bike-workshop.alvo.json")))));
        names.UnionWith(PropertyNames(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "schema", "project.schema.json")))));
        names.UnionWith(InstructionExamples.Parse(text).SelectMany(ExampleTokens));
        names.UnionWith(AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true));
        return names;
    }

    /// <summary>Every distinct snake-case token inside a code span of <paramref name="text"/>, fences left out.</summary>
    internal static IEnumerable<string> SnakeCaseInCode(string text) =>
        CodeSpan().Matches(Fence().Replace(text, string.Empty))
            .SelectMany(span => SnakeCase().Matches(span.Value).Select(match => match.Value))
            .Distinct();

    private static IEnumerable<string> Beyond(IReadOnlySet<string> columns, IReadOnlySet<string> always) =>
        columns.Where(column => !always.Contains(column));

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;

    private static IEnumerable<string> PropertyNames(JsonNode? node) => node switch
    {
        JsonObject members => members.SelectMany(member => PropertyNames(member.Value).Prepend(member.Key)),
        JsonArray items => items.SelectMany(PropertyNames),
        _ => [],
    };

    private static IEnumerable<string> ExampleTokens(InstructionExample example) =>
        example.Operations.EnumerateArray()
            .SelectMany(operation => _pointerMembers.Where(key => operation.TryGetProperty(key, out _)).Select(key => operation.GetProperty(key).GetString()!))
            .SelectMany(pointer => pointer.Split('/'));

    [GeneratedRegex(@"\b(?:get|check|propose|validate|apply|set|update|delete|list|add|remove|create)_[a-z_]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();

    [GeneratedRegex("^```json\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    internal static partial Regex JsonFence();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("`[^`\n]+`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCase();

    [GeneratedRegex("`(?<token>[a-z]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
