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
    private static readonly HashSet<string> _traits = new(StringComparer.Ordinal) { "tenancy", "audit", "softDelete" };

    /// <summary>The two skill tools, which are bounded apart from the management tools (D34).</summary>
    internal static IReadOnlySet<string> SkillTools { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName };

    /// <summary>Every area <see cref="AreaOf"/> can name, most specific first.</summary>
    internal static IReadOnlyList<string> Areas { get; } =
        [ProjectAccess, RulesAndCel, Hooks, Indexes, TraitsAndTenancy, ComputedAndRollups, EntitiesAndFields];

    /// <summary>The names of the skills the assistant is given.</summary>
    internal static IReadOnlyCollection<string> Catalogue { get; } = [.. EmbeddedSkills.All.Select(skill => skill.Frontmatter.Name)];

    /// <summary>The area a changed path belongs to, reading a new field's own declaration; <see langword="null"/> for none.</summary>
    internal static string? AreaOf(string path, JsonNode? proposed)
    {
        var segments = path.Split('/');
        return path switch
        {
            _ when path.StartsWith("/access", StringComparison.Ordinal) => ProjectAccess,
            _ when segments.Contains("rules") => RulesAndCel,
            _ when segments.Contains("hooks") => Hooks,
            _ when segments.Contains("indexes") => Indexes,
            _ when segments.Any(_traits.Contains) || path == "/tenancy" => TraitsAndTenancy,
            _ when segments.Contains("computed") || segments.Contains("rollup") || IsDerived(proposed) => ComputedAndRollups,
            _ when path.StartsWith("/entities/", StringComparison.Ordinal) => EntitiesAndFields,
            _ => null,
        };
    }

    /// <summary>The skills a turn's proposal needs, ordinal; none when it filed none.</summary>
    internal static IReadOnlyList<string> Needed(TurnRecord turn) =>
    [
        .. turn.ChangedPaths.Select(path => AreaOf(path, turn.Proposed(path))).OfType<string>().Distinct()
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

    private static bool IsDerived(JsonNode? proposed) =>
        proposed is JsonObject field && (field["computed"] is not null || field["rollup"] is not null);

    private static string? Skill(RecordedCall call) =>
        call.Arguments?.TryGetValue(SkillName, out var value) == true
            ? value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString()
            : null;
}
