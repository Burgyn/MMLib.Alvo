using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One number field of a child entity — what <c>sum</c>, <c>avg</c>, <c>min</c> and <c>max</c> can aggregate.</summary>
/// <param name="Name">The child field.</param>
/// <param name="Type">Integer or decimal.</param>
/// <param name="Precision">A decimal's precision, as declared.</param>
/// <param name="Scale">A decimal's scale, as declared.</param>
internal sealed record RollupChildField(string Name, FieldType Type, int? Precision, int? Scale);

/// <summary>An entity a rollup on the parent could aggregate.</summary>
/// <param name="Entity">The child entity.</param>
/// <param name="Via">Its ref fields that point at the parent, in declaration order.</param>
/// <param name="Numbers">Its integer and decimal fields, in declaration order.</param>
/// <param name="Refusal">Why the apply would refuse a rollup from it, or <see langword="null"/>.</param>
internal sealed record RollupSource(
    string Entity, IReadOnlyList<string> Via, IReadOnlyList<RollupChildField> Numbers, string? Refusal);

/// <summary>
/// The entities whose rows a rollup on <c>parent</c> could aggregate, read from the <b>working copy</b> — so an
/// entity staged a moment ago is offered too — with the ones the apply refuses kept in the list and said.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are <c>RollupResolver</c>'s own</b>: a child must reference the parent
/// (<c>RollupResolver.cs:252-279</c>), be physical (<c>:218-227</c>), and agree with it about tenancy, where an
/// entity's tenancy is its declared one or <c>scoped</c> when the project turns tenancy on (<c>:153-172</c>, via
/// <c>DescriptorToSchemaMapper.ResolveTenancy</c>). An entity with no ref here is not a source at all.
/// </para>
/// <para>
/// Read as JSON (the <c>DescriptorLens</c> rule): an unreadable document is no sources, never a throw.
/// </para>
/// </remarks>
internal static class RollupSources
{
    /// <summary>The sources, in the descriptor's own entity order.</summary>
    /// <param name="descriptorJson">The working descriptor.</param>
    /// <param name="parent">The entity the rollup field would be on.</param>
    public static IReadOnlyList<RollupSource> For(string descriptorJson, string parent)
    {
        if (Parse(descriptorJson) is not { } root || root["entities"] is not JsonObject entities
            || entities[parent] is not JsonObject declaring)
        {
            return [];
        }

        var enabled = root["tenancy"]?["enabled"] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

        return [.. entities
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => Source(pair.Key, (JsonObject)pair.Value!, parent, declaring, enabled))
            .OfType<RollupSource>()];
    }

    private static RollupSource? Source(string name, JsonObject child, string parent, JsonObject declaring, bool enabled)
    {
        var fields = child["fields"] as JsonObject ?? [];
        List<string> via = [.. fields
            .Where(field => Is(field.Value?["type"], "ref") && Is(field.Value?["entity"], parent))
            .Select(field => field.Key)];

        return via.Count == 0 ? null : new RollupSource(name, via, Numbers(fields), Refusal(name, child, parent, declaring, enabled));
    }

    private static List<RollupChildField> Numbers(JsonObject fields)
        => [.. fields
            .Where(field => Is(field.Value?["type"], "integer") || Is(field.Value?["type"], "decimal"))
            .Select(field => new RollupChildField(
                field.Key,
                Is(field.Value?["type"], "decimal") ? FieldType.Decimal : FieldType.Integer,
                Whole(field.Value?["precision"]),
                Whole(field.Value?["scale"])))];

    private static string? Refusal(string name, JsonObject child, string parent, JsonObject declaring, bool enabled)
    {
        if (Is(child["storage"], "dynamic"))
        {
            return $"{name} is a dynamic entity, which is not part of the applied schema — nothing would maintain this rollup.";
        }

        return Scoped(child, enabled) == Scoped(declaring, enabled)
            ? null
            : $"{parent} is {Word(Scoped(declaring, enabled))} and {name} is {Word(Scoped(child, enabled))}: the apply "
                + "refuses a rollup across tenancy, because one tenant's number would be computed from another's rows.";
    }

    private static bool Scoped(JsonObject entity, bool enabled)
        => entity["tenancy"] is JsonValue value && value.TryGetValue<string>(out var declared)
            ? declared == "scoped"
            : enabled;

    private static string Word(bool scoped) => scoped ? "scoped" : "global";

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;

    private static int? Whole(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
