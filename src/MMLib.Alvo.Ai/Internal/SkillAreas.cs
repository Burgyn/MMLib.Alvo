using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// Which descriptor skill a path belongs to — the one routing the eval's skill grade and a refusal's skill hint share
/// (D50), so the grade and the hint cannot disagree.
/// </summary>
internal static class SkillAreas
{
    internal const string ProjectAccess = "project-access";
    internal const string RulesAndCel = "rules-and-cel";
    internal const string Hooks = "hooks";
    internal const string Indexes = "indexes";
    internal const string TraitsAndTenancy = "traits-and-tenancy";
    internal const string ComputedAndRollups = "computed-and-rollups";
    internal const string EntitiesAndFields = "entities-and-fields";

    /// <summary>The entity keys that are traits: each is <c>traits-and-tenancy</c>'s.</summary>
    internal static IReadOnlySet<string> Traits { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "tenancy", "audit", "softDelete", "storage", "realtime" };

    /// <summary>
    /// Every column the framework manages on some entity — the union spec §3 pins — so a declared one is a traits
    /// question whichever trait adds it.
    /// </summary>
    private static readonly IReadOnlySet<string> _managedColumns =
        AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: true);

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
    /// The area of a violation's pointer (D50): <see cref="AreaOf"/>, except that a field named as a managed column is
    /// <c>traits-and-tenancy</c>'s — the skill that says which trait adds it.
    /// </summary>
    internal static string? ForViolation(string pointer) =>
        JsonPointer.TryParse(pointer, out var parsed)
        && parsed.Tokens is ["entities", _, "fields", var field, ..]
        && _managedColumns.Contains(field)
            ? TraitsAndTenancy
            : AreaOf(pointer, null);

    /// <summary>Whether a proposed field declares a derived value: <c>computed</c> or <c>rollup</c>.</summary>
    internal static bool IsDerived(JsonNode? proposed) =>
        proposed is JsonObject field && (field["computed"] is not null || field["rollup"] is not null);

    private static string EntityFacetArea(IReadOnlyList<string> tokens, JsonNode? proposed) => tokens[2] switch
    {
        "rules" => RulesAndCel,
        "hooks" => Hooks,
        "indexes" => Indexes,
        "fields" when tokens.Count == 4 && IsDerived(proposed) => ComputedAndRollups,
        "fields" when tokens.Count > 4 && tokens[4] is "computed" or "rollup" => ComputedAndRollups,
        var facet when Traits.Contains(facet) => TraitsAndTenancy,
        _ => EntitiesAndFields,
    };
}
