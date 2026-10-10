namespace MMLib.Alvo.DocsGen.Schema;

internal static class GuideMap
{
    internal const string DescriptorConcept = "concepts/descriptor";

    internal static IReadOnlyDictionary<string, string?> Guides { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["index"] = DescriptorConcept,
        ["branding"] = DescriptorConcept,
        ["tenancy"] = "guides/multi-tenancy",
        ["dynamic-entities"] = "concepts/dynamic-entities",
        ["auth"] = "guides/authentication",
        ["access"] = "start-here/coding-agents",
        ["entities"] = "guides/entities-and-fields",
        ["entities-fields"] = "guides/entities-and-fields",
        ["entities-computed-and-rollups"] = "guides/computed-and-rollups",
        ["entities-rules"] = "guides/access-rules",
        ["entities-hooks"] = "guides/before-hooks",
        ["entities-indexes"] = "guides/indexes",
        ["automation"] = null,
        ["templates"] = "guides/after-hooks-and-webhooks",
        ["webhooks"] = "guides/after-hooks-and-webhooks",
        ["formats"] = "guides/entities-and-fields",
        ["functions"] = null,
    };

    internal static string? GuideOf(string pageSlug) => Guides.TryGetValue(pageSlug, out var guide) ? guide : DescriptorConcept;
}
