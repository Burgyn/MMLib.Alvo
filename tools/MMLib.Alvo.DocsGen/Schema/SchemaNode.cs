using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaNode
{
    private const string DefinitionPrefix = "#/$defs/";
    private const int MaxReferenceHops = 32;
    private const int MaxLabelDepth = 8;

    private static readonly JsonSerializerOptions _literalJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyList<JsonObject> _chain;

    private SchemaNode(IReadOnlyList<JsonObject> chain, string? definition)
    {
        _chain = chain;
        Definition = definition;
    }

    internal string? Definition { get; }

    internal string Description =>
        _chain.Select(schema => schema["description"]?.GetValue<string>()).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";

    internal static SchemaNode? Resolve(JsonNode? node, JsonObject root)
    {
        if (node is not JsonObject schema)
        {
            return null;
        }

        var chain = new List<JsonObject> { schema };
        string? definition = null;
        while (chain.Count <= MaxReferenceHops && chain[^1]["$ref"]?.GetValue<string>() is { } reference)
        {
            definition = reference[DefinitionPrefix.Length..];
            chain.Add(root["$defs"]![definition]!.AsObject());
        }

        return new SchemaNode(chain, definition);
    }

    internal static string Literal(JsonNode? value) => value?.ToJsonString(_literalJson) ?? "null";

    internal JsonNode? Find(string keyword) => _chain.Select(schema => schema[keyword]).FirstOrDefault(value => value is not null);

    internal JsonObject? FindObject(string keyword) => Find(keyword) as JsonObject;

    internal IEnumerable<JsonObject> FindObjects(string keyword) => (Find(keyword) as JsonArray ?? []).OfType<JsonObject>();

    internal IReadOnlyList<string> FindStrings(string keyword) =>
        (Find(keyword) as JsonArray ?? []).Select(item => item?.GetValue<string>()).OfType<string>().ToList();

    internal IReadOnlyList<SchemaNode> Branches(JsonObject root) =>
        FindObjects("oneOf").Concat(FindObjects("anyOf")).Select(branch => Resolve(branch, root)!).ToList();

    internal IReadOnlyList<SchemaNode> ObjectBranches(JsonObject root) => Branches(root).Where(branch => branch.IsObject).ToList();

    internal bool HasProperties => (FindObject("properties") ?? []).Any(property => property.Value is JsonObject);

    internal bool IsObject => DeclaredTypes.Contains("object") || FindObject("properties") is not null;

    internal bool HasChildren(JsonObject root) =>
        HasProperties
        || FindObject("additionalProperties") is not null
        || FindObject("items") is not null
        || ObjectBranches(root).Count > 0;

    internal IReadOnlyList<string> Values()
    {
        var values = (Find("enum") as JsonArray ?? []).Select(Literal).ToList();
        if (Find("const") is { } constant)
        {
            values.Add(Literal(constant));
        }

        return values;
    }

    internal string TypeLabel(JsonObject root, int depth = 0)
    {
        var label = BaseLabel(root, depth);
        if (depth >= MaxLabelDepth)
        {
            return label;
        }

        if (label == "object" && !HasProperties && Resolve(FindObject("additionalProperties"), root) is { } entry)
        {
            return "map of " + entry.TypeLabel(root, depth + 1);
        }

        if (label == "array" && Resolve(FindObject("items"), root) is { } item)
        {
            return "array of " + item.TypeLabel(root, depth + 1);
        }

        return label;
    }

    internal static string Union(IEnumerable<string> labels)
    {
        var distinct = labels.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Contains("any") || distinct.Count == 0 ? "any" : string.Join(" or ", distinct);
    }

    private List<string> DeclaredTypes => Find("type") switch
    {
        JsonArray types => types.Select(type => type!.GetValue<string>()).ToList(),
        JsonValue type => [type.GetValue<string>()],
        _ => [],
    };

    private string BaseLabel(JsonObject root, int depth)
    {
        if (DeclaredTypes.Count > 0)
        {
            return string.Join(" or ", DeclaredTypes);
        }

        if (Find("const") is { } constant)
        {
            return KindLabel(constant);
        }

        if (Find("enum") is JsonArray values)
        {
            return values.All(value => value?.GetValueKind() == JsonValueKind.String) ? "string" : "any";
        }

        var branches = Branches(root);
        if (branches.Count > 0 && depth < MaxLabelDepth)
        {
            return Union(branches.Select(branch => branch.TypeLabel(root, depth + 1)));
        }

        return FindObject("properties") is not null ? "object" : "any";
    }

    private static string KindLabel(JsonNode constant) => constant.GetValueKind() switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };
}
