using System.Text.Json;
using System.Text.Json.Schema;

namespace MMLib.Alvo.Functions.Internal;

/// <summary>
/// A function input type's JSON Schema, and the three things the apply check reads from it: each top-level property's JSON
/// types and the required set.
/// </summary>
/// <param name="Document">The whole exported schema, as the catalog lists it.</param>
/// <param name="Properties">Each top-level property, by its wire name, with the JSON types it accepts.</param>
/// <param name="Required">The top-level properties an input must name.</param>
internal sealed record FunctionInputSchema(
    JsonElement Document, IReadOnlyDictionary<string, JsonTypes> Properties, IReadOnlySet<string> Required)
{
    private const JsonTypes AllTypes =
        JsonTypes.String | JsonTypes.Number | JsonTypes.Integer | JsonTypes.Boolean | JsonTypes.Object | JsonTypes.Array | JsonTypes.Null;

    private static readonly JsonSchemaExporterOptions _exporter = new() { TreatNullObliviousAsNonNullable = true };

    /// <summary>
    /// The one serializer configuration of function input: the schema is exported with it and the run binds with it, so what
    /// the schema calls required or non-nullable is what the binder refuses.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = Frozen();

    /// <summary>Exports <paramref name="input"/>'s schema.</summary>
    /// <param name="input">A function's input type.</param>
    internal static FunctionInputSchema For(Type input)
    {
        var node = SerializerOptions.GetJsonSchemaAsNode(input, _exporter);
        var document = JsonSerializer.SerializeToElement(node, SerializerOptions);

        return new(document, PropertiesOf(document), RequiredOf(document));
    }

    /// <summary>Whether <paramref name="property"/> accepts <paramref name="literal"/>; a property the schema lacks accepts nothing.</summary>
    internal bool Accepts(string property, JsonElement literal) =>
        Properties.TryGetValue(property, out var types) && Accepts(types, literal);

    /// <summary>Whether a property of <paramref name="types"/> accepts <paramref name="literal"/>'s JSON type.</summary>
    internal static bool Accepts(JsonTypes types, JsonElement literal) => literal.ValueKind switch
    {
        JsonValueKind.String => types.HasFlag(JsonTypes.String),
        JsonValueKind.Number => types.HasFlag(JsonTypes.Number) || (types.HasFlag(JsonTypes.Integer) && IsWhole(literal)),
        JsonValueKind.True or JsonValueKind.False => types.HasFlag(JsonTypes.Boolean),
        JsonValueKind.Object => types.HasFlag(JsonTypes.Object),
        JsonValueKind.Array => types.HasFlag(JsonTypes.Array),
        JsonValueKind.Null => types.HasFlag(JsonTypes.Null),
        _ => false,
    };

    private static JsonSerializerOptions Frozen()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    private static bool IsWhole(JsonElement number) => number.TryGetDecimal(out var value) && decimal.Truncate(value) == value;

    private static Dictionary<string, JsonTypes> PropertiesOf(JsonElement document) =>
        document.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().ToDictionary(property => property.Name, property => TypesOf(property.Value), StringComparer.Ordinal)
            : new Dictionary<string, JsonTypes>(StringComparer.Ordinal);

    private static HashSet<string> RequiredOf(JsonElement document) =>
        document.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static JsonTypes TypesOf(JsonElement property) =>
        property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("type", out var type) ? AllTypes
        : type.ValueKind is JsonValueKind.String ? Parse(type.GetString())
        : type.EnumerateArray().Aggregate(JsonTypes.None, (all, one) => all | Parse(one.GetString()));

    private static JsonTypes Parse(string? type) => type switch
    {
        "string" => JsonTypes.String,
        "number" => JsonTypes.Number,
        "integer" => JsonTypes.Integer,
        "boolean" => JsonTypes.Boolean,
        "object" => JsonTypes.Object,
        "array" => JsonTypes.Array,
        "null" => JsonTypes.Null,
        _ => JsonTypes.None,
    };
}

/// <summary>The JSON Schema type names a property accepts, as flags.</summary>
[Flags]
internal enum JsonTypes
{
    None = 0,
    String = 1,
    Number = 2,
    Integer = 4,
    Boolean = 8,
    Object = 16,
    Array = 32,
    Null = 64,
}
