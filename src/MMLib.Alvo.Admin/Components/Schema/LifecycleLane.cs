using MMLib.Alvo.Management;

using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One descriptor after-hook as the After commit lane draws it: its point, its place there, its JSON.</summary>
/// <param name="Point">The hook point, e.g. <c>afterUpdate</c>.</param>
/// <param name="Number">Its place among the point's hooks, from one.</param>
/// <param name="Json">The hook as declared.</param>
internal sealed record LaneHook(string Point, int Number, string Json);

/// <summary>
/// What runs on an entity before, inside and after a write's transaction, and the entity list's count of host code — the
/// logic behind the Lifecycle tab, the editors' note, the simulator's and the preview's code lists and the <c>⟨/⟩ n</c>
/// badge, kept out of the markup so it can be tested.
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

    /// <summary>The commands the Commands lane draws for <paramref name="entity"/>: those bound to a row of it.</summary>
    public static IReadOnlyList<ManagementExtension> Commands(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. extensions.Where(extension => extension.Kind == AlvoExtensionKind.Command && extension.Entities.Contains(entity, StringComparer.Ordinal))];

    /// <summary>
    /// Whether the Commands lane is drawn at all: only in a host that maps a command on some route (HX-08 Ruling 57), so
    /// the standalone container, which has no C# extension point, never shows a lane that can only be empty.
    /// </summary>
    public static bool HasMappedCommands(IReadOnlyList<ManagementExtension> extensions) =>
        extensions.Any(extension => extension.Kind == AlvoExtensionKind.Command && extension.Route is not null);

    /// <summary>The validate hooks the Before the transaction lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> BeforeTransaction(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Kind == AlvoExtensionKind.Validate)];

    /// <summary>The before/after write and delete hooks the In the transaction lane draws for <paramref name="entity"/>.</summary>
    public static IReadOnlyList<ManagementExtension> InTransaction(string entity, IReadOnlyList<ManagementExtension> extensions) =>
        [.. Binding(entity, extensions).Where(extension => extension.Stage == AlvoExtensionStage.InTransaction)];

    /// <summary>Whether the write lanes are drawn: only in a host that registers a write hook (Ruling 29, HX-08 Ruling 57's rule).</summary>
    public static bool HasWriteHooks(IReadOnlyList<ManagementExtension> extensions) => extensions.Any(IsWriteHook);

    /// <summary>Whether <paramref name="extension"/> is a write hook of any stage.</summary>
    public static bool IsWriteHook(ManagementExtension extension) => extension.Kind is AlvoExtensionKind.Validate or AlvoExtensionKind.BeforeWrite
        or AlvoExtensionKind.AfterWrite or AlvoExtensionKind.BeforeDelete or AlvoExtensionKind.AfterDelete;

    /// <summary>When, in a write, <paramref name="extension"/> runs, in two words.</summary>
    public static string StageLabel(ManagementExtension extension) => extension.Kind switch
    {
        AlvoExtensionKind.Validate => "validate",
        AlvoExtensionKind.BeforeWrite => "before write",
        AlvoExtensionKind.AfterWrite => "after write",
        AlvoExtensionKind.BeforeDelete => "before delete",
        AlvoExtensionKind.AfterDelete => "after delete",
        AlvoExtensionKind.Command => "command",
        _ => "after commit",
    };

    /// <summary>
    /// A write hook's numbers on this instance — its p95 and its rejections in the last day — or nothing without them, and
    /// nothing for any other code: rejections are a write hook's, and Ruling 29 scopes the numbers to the write lanes.
    /// </summary>
    public static string? Numbers(ManagementExtension extension, ManagementExtensionStats? stats) =>
        !IsWriteHook(extension) ? null
        : stats?.Items.FirstOrDefault(item => item.Id == extension.Id) is { P95: { } p95 } stat
            ? string.Create(CultureInfo.InvariantCulture, $"p95 {p95.TotalMilliseconds:0.#} ms · {stat.Rejections} rejection{(stat.Rejections == 1 ? "" : "s")} in 24 h")
            : null;

    /// <summary>The editors' note: how many write hooks also run on <paramref name="operation"/> — a switched-off one does not — or nothing.</summary>
    public static string? AlsoRuns(string entity, string operation, IReadOnlyList<ManagementExtension> extensions)
    {
        var count = Binding(entity, extensions).Count(extension => IsWriteHook(extension) && extension.Enabled && extension.Operations.Contains(operation, StringComparer.Ordinal));

        return count switch
        {
            0 => null,
            1 => "1 extension from code also runs on this operation",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} extensions from code also run on this operation"),
        };
    }

    /// <summary>The extensions bound to any of <paramref name="entities"/>, each once, in catalog order.</summary>
    /// <remarks>
    /// An empty name — a plan line the preview does not recognise — touches nothing, not even an every-entity hook. A rename's
    /// line carries only the new name, so the hooks bound to the old one are not listed; apply still refuses to unbind them.
    /// </remarks>
    public static IReadOnlyList<ManagementExtension> Touching(IEnumerable<string> entities, IReadOnlyList<ManagementExtension> extensions)
    {
        var touched = entities.Where(entity => entity.Length > 0).ToHashSet(StringComparer.Ordinal);
        return [.. extensions.Where(extension => touched.Any(entity => Binding(entity, [extension]).Count > 0))];
    }

    /// <summary>A command's route line: its method and pattern, or that it is invoked in process.</summary>
    public static string Route(ManagementExtension command) =>
        command.Route is { } route ? $"{command.HttpMethod ?? "POST"} {route}" : "invoked in process";

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
