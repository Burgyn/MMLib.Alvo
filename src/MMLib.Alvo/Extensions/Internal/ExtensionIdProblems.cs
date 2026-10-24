namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// The id rules of the one catalog namespace (spec 2026-10-14 E3): each id in its grammar, registered once — a retired
/// id included — and no two ids deriving one event-name segment.
/// </summary>
internal static class ExtensionIdProblems
{
    private const string IdFix =
        "Use lower-case letters, digits and hyphens, starting with a letter, at most 63 characters — 'send-confirmation'.";

    internal static IEnumerable<ExtensionProblem> Of(ExtensionRegistry registry) =>
        [.. registry.All.Where(registration => !Grammatical(registration)).Select(BadId), .. Duplicates(registry), .. Collisions(registry)];

    private static bool Grammatical(ExtensionRegistration registration) =>
        registration is RetiredExtension ? ExtensionIds.IsRetirableId(registration.Id) : ExtensionIds.IsExtensionId(registration.Id);

    private static ExtensionProblem BadId(ExtensionRegistration registration) =>
        new(registration.Id, KindOf(registration), IdLocator(registration.Id), $"'{registration.Id}' is not an extension id.", IdFix);

    private static IEnumerable<ExtensionProblem> Duplicates(ExtensionRegistry registry) =>
        registry.All.GroupBy(registration => registration.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ExtensionProblem(
                group.Key,
                KindOf(group.First()),
                IdLocator(group.Key),
                $"'{group.Key}' is registered {group.Count()} times ({string.Join(", ", group.Select(KindOf))}).",
                "Give each registration its own id: an id is how its deliveries are found again after a restart, and a retired id may not be registered."));

    private static IEnumerable<ExtensionProblem> Collisions(ExtensionRegistry registry) =>
        registry.All.Select(registration => registration.Id).Distinct(StringComparer.Ordinal)
            .GroupBy(ExtensionIds.DerivedSegment, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => Collision([.. group], group.Key, registry));

    private static IEnumerable<ExtensionProblem> Collision(IReadOnlyList<string> ids, string segment, ExtensionRegistry registry) =>
        Blamed(ids, registry).Select(blamed => new ExtensionProblem(
            blamed.Id,
            KindOf(blamed),
            IdLocator(blamed.Id),
            $"'{string.Join("' and '", ids)}' derive one event-name segment, '{segment}', so the events Alvo mints for them could not be told apart.",
            "Rename one of them."));

    private static List<ExtensionRegistration> Blamed(IReadOnlyList<string> ids, ExtensionRegistry registry)
    {
        var colliding = registry.All
            .Where(registration => ids.Contains(registration.Id, StringComparer.Ordinal))
            .DistinctBy(registration => registration.Id, StringComparer.Ordinal)
            .ToList();
        List<ExtensionRegistration> handlers = [.. colliding.OfType<HandlerRegistration>()];

        return handlers.Count > 0 ? handlers : [colliding[0]];
    }

    private static string KindOf(ExtensionRegistration registration) =>
        registration is RetiredExtension ? ExtensionBindingCompiler.RetiredKind : ExtensionBindingCompiler.HandlerKind;

    private static string IdLocator(string id) => $"extensions/{id}/id";
}
