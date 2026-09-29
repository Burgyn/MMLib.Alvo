using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>
/// The facts the instructions state about names — the pattern, the reserved names and the managed columns — as data,
/// so one suite can hold them to the schema and the ports, and another to the real validator.
/// </summary>
internal static partial class InstructionClaims
{
    /// <summary>The managed-column bullet for the columns every entity carries.</summary>
    internal const string EveryEntity = "on every entity";

    /// <summary>The managed-column bullet for a scoped entity.</summary>
    internal const string ScopedEntity = "on an entity whose `tenancy` is `scoped`";

    /// <summary>The managed-column bullet for an audited entity.</summary>
    internal const string AuditedEntity = "on an entity with `\"audit\": true`";

    /// <summary>The managed-column bullet for a soft-deleted entity.</summary>
    internal const string SoftDeletedEntity = "on an entity with `\"softDelete\": true`";

    private const string FieldsMarker = "the fields";

    /// <summary>The four managed-column bullets' labels, in the order the instructions state them.</summary>
    internal static IReadOnlyList<string> TraitLabels { get; } = [EveryEntity, ScopedEntity, AuditedEntity, SoftDeletedEntity];

    internal static string NamePattern(string markdown) =>
        Pattern().Match(Line(markdown, "- **Names**:")).Groups["pattern"].Value;

    internal static IReadOnlyList<string> ReservedFields(string markdown)
    {
        var line = Line(markdown, "- Reserved names:");
        return [.. Token().Matches(line[line.IndexOf(FieldsMarker, StringComparison.Ordinal)..]).Select(match => match.Groups["token"].Value)];
    }

    internal static Dictionary<string, IReadOnlyList<string>> ManagedColumns(string markdown) =>
        TraitBullet().Matches(markdown).ToDictionary(
            match => match.Groups["label"].Value,
            match => (IReadOnlyList<string>)[.. Token().Matches(match.Groups["columns"].Value).Select(token => token.Groups["token"].Value)],
            StringComparer.Ordinal);

    private static string Line(string markdown, string prefix) =>
        markdown.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal)).TrimEnd('\r');

    [GeneratedRegex(@"`(?<pattern>\^[^`]+\$)`", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex("`(?<token>[a-z][a-z_]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^  - (?<label>on [^\r\n]+?) — (?<columns>[^\r\n]+)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TraitBullet();
}
