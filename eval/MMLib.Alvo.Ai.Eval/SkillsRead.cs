using Microsoft.Agents.AI;
using MMLib.Alvo.Ai.Internal;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Whether a turn loaded the skill of every area its proposal touches, before its first dry run (D31).</summary>
/// <remarks>
/// <para>
/// "Before" means an earlier round. A <c>load_skill</c> in the same model response as the first dry run was chosen
/// before the model had read a word of it.
/// </para>
/// <para>
/// <b>The first dry run of any kind, not the first one touching the area</b> (D40, stricter than D31's wording). A
/// turn's dry runs are attempts at one proposal, so the skill that proposal needs is owed before the first of them:
/// a model that dry-runs, is refused, and only then loads the skill has spent an attempt on what the skill says.
/// </para>
/// <para>
/// The area is read from the changed path, and for a new field from its value too: <c>/entities/x/fields/y</c> is a
/// computed field or a rollup only by what it declares. <c>field-types-and-formats</c> is never required. A field's type
/// is in every proposal, and the base prompt states the types. Nor is <c>capabilities-and-limits</c>: it is what an
/// answer that proposes nothing needs, and this grader only judges proposals.
/// </para>
/// </remarks>
internal static class SkillsRead
{
    private const string Prefix = "alvo-descriptor-";
    private const string SkillName = "skillName";
    private const string ProjectAccess = "project-access";
    private const string RulesAndCel = "rules-and-cel";
    private const string Hooks = "hooks";
    private const string Indexes = "indexes";
    private const string TraitsAndTenancy = "traits-and-tenancy";
    private const string ComputedAndRollups = "computed-and-rollups";
    private const string EntitiesAndFields = "entities-and-fields";

    private static readonly HashSet<string> _dryRuns = new(StringComparer.Ordinal) { "check_change", "propose_change" };
    private static readonly HashSet<string> _traits = new(StringComparer.Ordinal) { "tenancy", "audit", "softDelete", "storage", "realtime" };

    /// <summary>The two skill tools, which are bounded apart from the management tools (D34).</summary>
    internal static IReadOnlySet<string> SkillTools { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName };

    /// <summary>Every area <see cref="AreaOf"/> can name, most specific first.</summary>
    internal static IReadOnlyList<string> Areas { get; } =
        [ProjectAccess, RulesAndCel, Hooks, Indexes, TraitsAndTenancy, ComputedAndRollups, EntitiesAndFields];

    /// <summary>The names of the skills the assistant is given.</summary>
    internal static IReadOnlyCollection<string> Catalogue { get; } = [.. EmbeddedSkills.All.Select(skill => skill.Frontmatter.Name)];

    /// <summary>
    /// The area a changed path itself belongs to, read by the position of its segments, and for a new field by its own
    /// declaration; <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// <b>By position, not by name anywhere in the path</b>: <c>/entities/{entity}/{facet}</c> and
    /// <c>/entities/{entity}/fields/{field}/{facet}</c>. An entity or a field that happens to be called <c>audit</c>,
    /// <c>rules</c> or <c>computed</c> is still an entity or a field.
    /// </remarks>
    internal static string? AreaOf(string path, JsonNode? proposed)
    {
        if (!JsonPointer.TryParse(path, out var pointer) || pointer.IsRoot)
        {
            return null;
        }

        var tokens = pointer.Tokens;
        return tokens[0] switch
        {
            "access" => ProjectAccess,
            "tenancy" => TraitsAndTenancy,
            "entities" => tokens.Count < 3 ? EntitiesAndFields : EntityFacetArea(tokens, proposed),
            _ => null,
        };
    }

    /// <summary>
    /// Every area a changed path needs: its own, and — when it adds a whole entity, the entity map, or a fields map —
    /// every area the proposed subtree declares, beside <c>entities-and-fields</c>.
    /// </summary>
    /// <remarks>Pure and total: a proposed value of any shape is read by pattern, never by an indexer that throws.</remarks>
    internal static IReadOnlyList<string> AreasOf(string path, JsonNode? proposed)
    {
        var areas = new List<string>();
        if (AreaOf(path, proposed) is { } own)
        {
            areas.Add(own);
        }

        if (JsonPointer.TryParse(path, out var pointer) && pointer.Tokens.Count is > 0 and < 4 && pointer.Tokens[0] == "entities")
        {
            areas.AddRange(SubtreeAreas(pointer.Tokens, proposed));
        }

        return [.. areas.Distinct(StringComparer.Ordinal)];
    }


    /// <summary>The skills a turn's proposal needs, ordinal; none when it filed none.</summary>
    internal static IReadOnlyList<string> Needed(TurnRecord turn) =>
    [
        .. turn.ChangedPaths.SelectMany(path => AreasOf(path, turn.Proposed(path))).Distinct(StringComparer.Ordinal)
            .Select(area => Prefix + area).Order(StringComparer.Ordinal),
    ];

    /// <summary>Passes when every skill <see cref="Needed"/> names was loaded in a round before the first dry run.</summary>
    internal static Verdict Judge(TurnRecord turn)
    {
        var firstDryRun = turn.Calls.Where(call => _dryRuns.Contains(call.Tool)).Select(call => call.Round).DefaultIfEmpty(int.MaxValue).Min();
        var loaded = turn.Calls.Where(call => call.Tool == AgentSkillsProvider.LoadSkillToolName && call.Round < firstDryRun)
            .Select(Skill).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var needed = Needed(turn);
        return Verdict.When(
            needed.All(loaded.Contains),
            $"skillsNeeded=[{string.Join(",", needed)}] skillsLoaded=[{string.Join(",", loaded.Order(StringComparer.Ordinal))}]");
    }

    private static IEnumerable<string> SubtreeAreas(IReadOnlyList<string> tokens, JsonNode? proposed) => tokens.Count switch
    {
        1 => EntitiesMapAreas(proposed),
        2 => EntityAreas(proposed),
        _ when tokens[2] == "fields" => FieldsMapAreas(proposed),
        _ => [],
    };

    private static string EntityFacetArea(IReadOnlyList<string> tokens, JsonNode? proposed) => tokens[2] switch
    {
        "rules" => RulesAndCel,
        "hooks" => Hooks,
        "indexes" => Indexes,
        "fields" when tokens.Count == 4 && IsDerived(proposed) => ComputedAndRollups,
        "fields" when tokens.Count > 4 && tokens[4] is "computed" or "rollup" => ComputedAndRollups,
        var facet when _traits.Contains(facet) => TraitsAndTenancy,
        _ => EntitiesAndFields,
    };

    private static IEnumerable<string> EntitiesMapAreas(JsonNode? proposed) =>
        proposed is JsonObject entities ? entities.SelectMany(entity => EntityAreas(entity.Value)) : [];

    private static IEnumerable<string> EntityAreas(JsonNode? proposed) =>
        proposed is JsonObject entity
            ? entity.SelectMany(member => member.Key == "fields" ? FieldsMapAreas(member.Value) : EntityMemberAreas(member.Key)).Append(EntitiesAndFields)
            : [];

    private static IEnumerable<string> EntityMemberAreas(string key) => key switch
    {
        "rules" => [RulesAndCel],
        "hooks" => [Hooks],
        "indexes" => [Indexes],
        _ when _traits.Contains(key) => [TraitsAndTenancy],
        _ => [],
    };

    private static IEnumerable<string> FieldsMapAreas(JsonNode? proposed) =>
        proposed is JsonObject fields
            ? fields.Where(field => IsDerived(field.Value)).Select(_ => ComputedAndRollups).Append(EntitiesAndFields)
            : [];

    private static bool IsDerived(JsonNode? proposed) =>
        proposed is JsonObject field && (field["computed"] is not null || field["rollup"] is not null);

    private static string? Skill(RecordedCall call) =>
        call.Arguments?.TryGetValue(SkillName, out var value) == true
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString()
            : null;
}
