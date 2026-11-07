using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One input property of a host function, as the hook editor holds it while it is typed.</summary>
/// <remarks>
/// <b>Required means "must be typed".</b> A property the function's schema lists as required but whose type admits
/// <c>null</c> is optional here: left empty it is written as <c>null</c>, which the apply accepts. A property the schema does
/// not require is omitted when left empty.
/// </remarks>
internal sealed class FunctionInputRow
{
    /// <summary>Initializes a row.</summary>
    /// <param name="name">The input property.</param>
    /// <param name="types">The JSON types its schema admits, or empty when it names none (or the property is not declared).</param>
    /// <param name="required">Whether the function cannot be called without a value for it.</param>
    public FunctionInputRow(string name, IReadOnlyList<string> types, bool required)
    {
        Name = name;
        Types = types;
        Required = required;
    }

    private string _text = string.Empty;

    /// <summary>A row opened from a declared value: a tagged expression, or a literal kept as declared.</summary>
    /// <param name="name">The input property.</param>
    /// <param name="value">The declared value; not modified.</param>
    /// <returns>The row, its types unknown until <see cref="Describe"/>.</returns>
    public static FunctionInputRow Opened(string name, JsonNode? value)
    {
        if (value is JsonObject tagged && tagged["$cel"] is JsonValue cel && cel.TryGetValue<string>(out var source))
        {
            return new FunctionInputRow(name, [], required: false) { Mode = MutateMode.Expression, Text = source };
        }

        var row = new FunctionInputRow(name, [], required: false)
        {
            Text = value is JsonValue text && text.TryGetValue<string>(out var literal) ? literal : value?.ToJsonString() ?? "null",
        };
        (row.IsDeclared, row.Declared) = (true, value?.DeepClone());
        return row;
    }

    /// <summary>The same row, as the function's schema describes it — what was typed or opened kept.</summary>
    /// <param name="described">The schema's row of the same name.</param>
    /// <returns>A row with <paramref name="described"/>'s types and requirement, and this row's value.</returns>
    public FunctionInputRow Describe(FunctionInputRow described)
    {
        ArgumentNullException.ThrowIfNull(described);
        var row = new FunctionInputRow(Name, described.Types, described.Required)
        {
            Mode = Mode,
            Text = Text,
            WritesNullWhenEmpty = described.WritesNullWhenEmpty,
        };
        (row.IsDeclared, row.Declared) = (IsDeclared, Declared);
        return row;
    }

    /// <summary>Gets the input property.</summary>
    public string Name { get; }

    /// <summary>Gets the JSON types its schema admits, in the schema's order; empty when the schema names none.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>Gets a value indicating whether the row must be typed before the action can be built.</summary>
    public bool Required { get; }

    /// <summary>Gets or sets how the row writes its value: a literal, or a <c>{"$cel": "…"}</c> expression.</summary>
    public MutateMode Mode { get; set; } = MutateMode.Literal;

    /// <summary>Gets or sets the literal as typed, or the CEL; setting it forgets <see cref="Declared"/>.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            IsDeclared = false;
            Declared = null;
        }
    }

    /// <summary>Gets a value indicating whether the row still holds the literal it was opened with, untyped over.</summary>
    /// <remarks>
    /// <see cref="MutateRow.DeclaredKind"/>'s reason: a declared <c>"3"</c> reads as the text <c>3</c>, and writing it back by
    /// the row's types would turn a string into a number nobody typed. Held, it is written back exactly as declared.
    /// </remarks>
    public bool IsDeclared { get; private set; }

    /// <summary>Gets the literal the row was opened with while <see cref="IsDeclared"/>; <see langword="null"/> is JSON <c>null</c>.</summary>
    public JsonNode? Declared { get; private set; }

    /// <summary>Gets a value indicating whether an empty row is written as <c>null</c>: the schema requires it and admits null.</summary>
    public bool WritesNullWhenEmpty { get; init; }

    /// <summary>Gets a value indicating whether the schema admits <c>null</c> for it.</summary>
    public bool Nullable => Types.Contains("null");

    /// <summary>Gets the placeholder its box shows: the JSON types it takes, such as <c>integer</c> or <c>string or null</c>.</summary>
    public string Placeholder => Types.Count == 0 ? "any JSON value" : string.Join(" or ", Types);
}

/// <summary>
/// The rows of a host function's input form, read from the input JSON Schema its <c>AddFunction</c> registration exports —
/// and the value each row writes into the action's <c>input</c>.
/// </summary>
/// <remarks>
/// Top-level <c>properties</c>, <c>required</c> and <c>type</c> only: the same three keywords the apply checks a function
/// input against, so the form never asks for less, or more, than the apply would refuse.
/// </remarks>
internal static class FunctionInputRows
{
    /// <summary>One row per declared property, required ones first, then by name.</summary>
    /// <param name="inputSchema">The function's input schema, as the extension catalog serves it.</param>
    /// <returns>The rows; empty for a schema with no <c>properties</c>.</returns>
    public static List<FunctionInputRow> From(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var required = RequiredOf(inputSchema);
        return [.. properties.EnumerateObject()
            .Select(property => Row(property, required))
            .OrderBy(row => row.Required ? 0 : 1)
            .ThenBy(row => row.Name, StringComparer.Ordinal)];
    }

    /// <summary>The value a typed row writes, or why it cannot be written.</summary>
    /// <param name="row">The row, with something typed in it.</param>
    /// <param name="value">The JSON value; <see langword="null"/> stands for JSON <c>null</c>.</param>
    /// <param name="refusal">Why the text fits none of the row's types, when it does not.</param>
    /// <returns>Whether the text converts.</returns>
    public static bool TryLiteral(FunctionInputRow row, out JsonNode? value, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(row);
        refusal = null;
        if (row.IsDeclared)
        {
            value = row.Declared?.DeepClone();
            return true;
        }

        if (Convert(row.Types, row.Text.Trim(), out value))
        {
            return true;
        }

        refusal = $"'{row.Name}' takes {row.Placeholder}; '{row.Text.Trim()}' is not one.";
        return false;
    }

    private static FunctionInputRow Row(JsonProperty property, HashSet<string> required)
    {
        var types = TypesOf(property.Value);
        var listed = required.Contains(property.Name);
        var nullable = types.Contains("null");
        return new FunctionInputRow(property.Name, types, listed && !nullable) { WritesNullWhenEmpty = listed && nullable };
    }

    private static HashSet<string> RequiredOf(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? [.. required.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!)]
            : [];

    private static List<string> TypesOf(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.String).Select(one => one.GetString()!)],
            _ => [],
        };
    }

    /// <summary>
    /// Converts the text by the first type that takes it, in a fixed order: integer, number, boolean, string, JSON. With no
    /// types known, JSON when it parses and text otherwise — the apply judges it.
    /// </summary>
    /// <returns>Whether it converted; a JSON <c>null</c> converts with <paramref name="value"/> <see langword="null"/>.</returns>
    private static bool Convert(IReadOnlyList<string> types, string text, out JsonNode? value)
    {
        value = null;
        if (types.Count == 0)
        {
            value = TryJson(types, text, out var parsed) ? parsed : JsonValue.Create(text);
            return true;
        }

        if (types.Contains("integer") && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            value = JsonValue.Create(whole);
        }
        else if (types.Contains("number") && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            value = JsonValue.Create(number);
        }
        else if (types.Contains("boolean") && bool.TryParse(text, out var flag))
        {
            value = JsonValue.Create(flag);
        }
        else if (types.Contains("string"))
        {
            value = JsonValue.Create(text);
        }
        else
        {
            return TryJson(types, text, out value);
        }

        return true;
    }

    private static bool TryJson(IReadOnlyList<string> types, string text, out JsonNode? value)
    {
        value = null;
        try
        {
            value = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        return value switch
        {
            null => types.Count == 0 || types.Contains("null"),
            JsonObject => types.Count == 0 || types.Contains("object"),
            JsonArray => types.Count == 0 || types.Contains("array"),
            _ => types.Count == 0,
        };
    }
}
