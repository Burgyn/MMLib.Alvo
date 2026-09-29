using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One field of a child entity, as a rollup on the parent would see it.</summary>
/// <param name="Name">The child field.</param>
/// <param name="Type">Its declared type.</param>
/// <param name="Precision">A decimal's precision, as declared.</param>
/// <param name="Scale">A decimal's scale, as declared.</param>
internal sealed record RollupChildField(string Name, FieldType Type, int? Precision, int? Scale);

/// <summary>An entity a rollup on the parent could aggregate.</summary>
/// <param name="Entity">The child entity.</param>
/// <param name="Via">Its ref fields that point at the parent, in declaration order.</param>
/// <param name="Numbers">Its integer and decimal fields, in declaration order — what a new rollup is offered.</param>
/// <param name="Refusal">Why the apply would refuse a rollup from it, or <see langword="null"/>.</param>
internal sealed record RollupSource(
    string Entity, IReadOnlyList<string> Via, IReadOnlyList<RollupChildField> Numbers, string? Refusal)
{
    /// <summary>Every field it declares, of any type, in declaration order.</summary>
    /// <remarks>
    /// Wider than <see cref="Numbers"/> because the apply is: <c>RollupResolver.EnsureAggregatedFieldIsResolvable</c>
    /// asks only that the aggregated field exists on the child, and <c>RollupRecompute</c> renders a plain
    /// <c>AVG</c>/<c>MIN</c>/<c>MAX</c> over any column. An existing rollup over one of these must stay savable.
    /// </remarks>
    public IReadOnlyList<RollupChildField> Fields { get; init; } = Numbers;
}

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

        if (via.Count == 0)
        {
            return null;
        }

        var declared = Declared(fields);
        return new RollupSource(
            name, via, [.. declared.Where(field => field.Type is FieldType.Integer or FieldType.Decimal)],
            Refusal(name, child, parent, declaring, enabled))
        { Fields = declared };
    }

    /// <summary>Every child field with its type; an unreadable type reads as a string, the schema's default.</summary>
    private static List<RollupChildField> Declared(JsonObject fields)
        => [.. fields
            .Where(field => field.Value is JsonObject)
            .Select(field => new RollupChildField(
                field.Key,
                Enum.TryParse<FieldType>(Text(field.Value!["type"]), ignoreCase: true, out var type) ? type : FieldType.String,
                Whole(field.Value!["precision"]),
                Whole(field.Value!["scale"])))];

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
