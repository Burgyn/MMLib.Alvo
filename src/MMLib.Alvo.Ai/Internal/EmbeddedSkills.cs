using Microsoft.Agents.AI;

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>The descriptor skills (D33), embedded from <c>.claude/skills/alvo-descriptor-*</c> and fixed.</summary>
/// <remarks>
/// <para>
/// <b>Embedded rather than read from disk, for #29 §5's reason</b> (D23): a deployment must not be able to edit what
/// the assistant believes. They are the same files Claude Code reads in this repository, so a rule the assistant
/// learns is one a developer's agent learns too.
/// </para>
/// <para>
/// <b>A skill's resources are the schema slices its body cites</b> (D26). <c>schema/project.schema.json#/…</c> is a
/// repository path Claude Code opens, and the name the assistant passes to <c>read_skill_resource</c>. Both resolve to
/// one file, linked into this assembly, never copied.
/// </para>
/// <para>
/// <b>Declaration order is initialisation order.</b> <c>_schema</c>, then <see cref="Files"/>, then
/// <see cref="All"/>, then <see cref="Provider"/>: building <see cref="All"/> already slices the schema.
/// </para>
/// </remarks>
internal static partial class EmbeddedSkills
{
    internal const string SkillsPrefix = "MMLib.Alvo.Ai.Skills/";
    internal const string SchemaResourceName = "MMLib.Alvo.Ai.Schema/project.schema.json";
    internal const string SchemaReference = "schema/project.schema.json#";

    private const string SkillFile = "/SKILL.md";
    private const string SliceDescription = "A slice of the descriptor's JSON Schema, schema/project.schema.json.";

    /// <summary>The skill list's frame in the instructions; the framework's default also advertises scripts (D28).</summary>
    private const string Catalogue =
        "## Skills\n\nEach skill below holds the rules of one area of the descriptor. Before `check_change` or "
        + "`propose_change` in an area, load its skill with `load_skill`; read a resource it lists with "
        + "`read_skill_resource`, using the name exactly as listed.\n\n<available_skills>\n{skills}\n</available_skills>";

    private static readonly Lazy<JsonNode> _schema = new(() => JsonNode.Parse(Read(SchemaResourceName))!);

    /// <summary>Every embedded skill file, by its path under <c>.claude/skills/</c>, as LF text.</summary>
    internal static IReadOnlyDictionary<string, string> Files { get; } = LoadFiles();

    /// <summary>The skills, one per <c>SKILL.md</c>, in path order.</summary>
    internal static IReadOnlyList<DescriptorSkill> All { get; } =
        [.. Files.Keys.Where(path => path.EndsWith(SkillFile, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(Build)];

    /// <summary>The context provider the agent is given: the skill list, <c>load_skill</c> and <c>read_skill_resource</c>.</summary>
    internal static AIContextProvider Provider { get; } = new ReadOnlySkillsProvider(new AgentSkillsProvider(All, new AgentSkillsProviderOptions
    {
        DisableLoadSkillApproval = true,
        DisableReadSkillResourceApproval = true,
        SkillsInstructionPrompt = Catalogue,
    }));

    /// <summary>The JSON Pointers a body cites after <see cref="SchemaReference"/>, each once, in order.</summary>
    internal static IReadOnlyList<string> SchemaPointers(string body) =>
        [.. Citation().Matches(body).Select(match => match.Groups["pointer"].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The slice of the embedded schema at <paramref name="pointer"/>, as JSON; throws when it names nothing.</summary>
    internal static string Slice(string pointer) =>
        Walk(pointer)?.ToJsonString(ToolJson.Options)
        ?? throw new InvalidOperationException($"'{SchemaReference}{pointer}' names nothing in the schema.");

    private static JsonNode? Walk(string pointer) =>
        JsonPointer.TryParse(pointer, out var parsed)
            ? parsed.Tokens.Aggregate<string, JsonNode?>(_schema.Value, Child)
            : null;

    private static JsonNode? Child(JsonNode? node, string token) => node switch
    {
        JsonObject members => members[token],
        JsonArray items when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < items.Count => items[index],
        _ => null,
    };

    private static DescriptorSkill Build(string path)
    {
        var parts = SkillMarkdown.Parse(Files[path]);
        return new DescriptorSkill(parts, [.. SchemaPointers(parts.Body).Select(SliceResource)]);
    }

    private static DescriptorSkillResource SliceResource(string pointer) =>
        new(SchemaReference + pointer, Slice(pointer), SliceDescription);

    private static Dictionary<string, string> LoadFiles() =>
        typeof(EmbeddedSkills).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(SkillsPrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[SkillsPrefix.Length..].Replace('\\', '/'), Read, StringComparer.Ordinal);

    private static string Read(string resource)
    {
        var assembly = typeof(EmbeddedSkills).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded resource '{resource}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    [GeneratedRegex(@"schema/project\.schema\.json#(?<pointer>/[^\s`)]*[^\s`).,;:])", RegexOptions.CultureInvariant)]
    private static partial Regex Citation();
}
