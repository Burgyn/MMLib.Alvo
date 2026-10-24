using MMLib.Alvo.Management;

using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One descriptor after-hook as the After commit lane draws it: its point, its place there, its JSON.</summary>
/// <param name="Point">The hook point, e.g. <c>afterUpdate</c>.</param>
/// <param name="Number">Its place among the point's hooks, from one.</param>
/// <param name="Json">The hook as declared.</param>
internal sealed record LaneHook(string Point, int Number, string Json);

/// <summary>
/// What runs on an entity after a commit, and the entity list's count of host code — the logic behind the Lifecycle tab
/// and the <c>⟨/⟩ n</c> badge, kept out of the markup so it can be tested.
/// </summary>
internal static class LifecycleLane
{
    private const string AllEntities = "*";
    private const string Separator = " · ";

    /// <summary>The descriptor's after-commit hook points, in the order the lane draws them.</summary>
    public static IReadOnlyList<string> AfterCommitPoints { get; } = ["afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>The descriptor's after-hooks, from the working copy's hooks of one entity, point by point.</summary>
    public static IReadOnlyList<LaneHook> AfterCommitHooks(IReadOnlyList<KeyValuePair<string, string>> hooks) =>
        [.. AfterCommitPoints.SelectMany(point => hooks.Where(pair => pair.Key == point).SelectMany(pair => Declared(point, pair.Value)))];

    /// <summary>The extensions that run on <paramref name="entity"/>: its own, and every every-entity one.</summary>
    public static IReadOnlyList<ManagementExtension> Binding(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. extensions.Where(extension =>
            extension.Entities.Contains(entity, StringComparer.Ordinal) || extension.Entities.Contains(AllEntities, StringComparer.Ordinal))];

    /// <summary>The extensions the After commit lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> AfterCommit(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Stage == AlvoExtensionStage.PostCommit)];

    /// <summary>One line saying where, when, how long and as whom an extension runs.</summary>
    public static string Summary(ManagementExtension extension) => string.Join(Separator, Parts(extension));

    /// <summary>What the badge says to a screen reader and in its tooltip.</summary>
    public static string BadgeLabel(int count) => count == 1
        ? "1 extension from code runs on this entity"
        : string.Create(CultureInfo.InvariantCulture, $"{count} extensions from code run on this entity");

    private static IEnumerable<LaneHook> Declared(string point, string json) =>
        HooksTab.Declared(json).Select((hook, index) => new LaneHook(point, index + 1, hook));

    private static IEnumerable<string> Parts(ManagementExtension extension)
    {
        if (extension.Entities.Contains(AllEntities, StringComparer.Ordinal))
        {
            yield return "every entity";
        }

        if (extension.Operations.Count > 0)
        {
            yield return "on " + string.Join(" or ", extension.Operations);
        }

        if (extension.Timeout is { } timeout)
        {
            yield return "timeout " + Duration(timeout);
        }

        if (extension.RunAs is { } runAs)
        {
            yield return RunsAs(runAs);
        }

        if (extension.Ordering is { } ordering)
        {
            yield return InOrder(ordering);
        }
    }

    private static string RunsAs(string runAs) => runAs switch
    {
        "eventActor" => "runs as the event's actor",
        "system" => "runs as the system",
        _ => "runs as " + runAs,
    };

    private static string InOrder(string ordering) => ordering switch
    {
        "none" => "in any order",
        "perRecord" => "in order per record",
        _ => "ordering " + ordering,
    };

    private static string Duration(TimeSpan value) =>
        value < TimeSpan.FromMinutes(1) ? Whole(value.TotalSeconds, "s")
        : value < TimeSpan.FromHours(1) ? Whole(value.TotalMinutes, "min")
        : Whole(value.TotalHours, "h");

    private static string Whole(double amount, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:0.##} {unit}");
}
