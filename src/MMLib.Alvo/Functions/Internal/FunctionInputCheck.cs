using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Internal;

using System.Text.Json;

namespace MMLib.Alvo.Functions.Internal;

/// <summary>
/// Checks a <c>function</c> action's object input against the function's exported input schema: every property it names
/// exists, every literal is of a JSON type the property accepts, and every required property is present. Top level only —
/// a nested typo is the run's <c>function-input-invalid</c>.
/// </summary>
internal static class FunctionInputCheck
{
    /// <summary>Every problem of <paramref name="fields"/> as an input of <paramref name="function"/>.</summary>
    /// <param name="path">The action's JSON pointer; problems are reported under its <c>input</c>.</param>
    /// <param name="function">The function the action calls.</param>
    /// <param name="fields">The input's properties as written.</param>
    internal static IEnumerable<DescriptorValidationError> Check(
        string path, FunctionRegistration function, IReadOnlyDictionary<string, ValueOrExpr?> fields)
    {
        var schema = function.Schema;
        foreach (var (name, value) in fields)
        {
            if (!schema.Properties.TryGetValue(name, out var types))
            {
                yield return Unknown(path, function.Id, name, schema.Properties.Keys);
            }
            else if (value?.Literal is { } literal && !FunctionInputSchema.Accepts(types, literal))
            {
                yield return WrongType(path, function.Id, name, types, literal.ValueKind);
            }
        }

        foreach (var missing in schema.Required.Where(name => !fields.ContainsKey(name)).Order(StringComparer.Ordinal))
        {
            yield return Error($"{path}/input", $"Function '{function.Id}' requires '{missing}' in its input.", $"Add '{missing}' to 'input'.");
        }
    }

    /// <summary>Whether a CEL value of <paramref name="type"/> binds to a property of <paramref name="types"/>.</summary>
    internal static bool Accepts(JsonTypes types, CelValueType type) => (types & JsonTypesOf(type)) != JsonTypes.None;

    /// <summary>The refusal of an expression whose result a property cannot take.</summary>
    internal static DescriptorValidationError WrongExpressionType(string path, string function, string name, JsonTypes types, CelValueType type) =>
        Error(
            $"{path}/input/{name}",
            $"'{name}' evaluates to {type}, and function '{function}' takes {Describe(types)} there.",
            $"Write a {Describe(types)} value, or a {{\"$cel\": \"…\"}} that yields one.");

    /// <summary>The JSON types each property accepts, spelled as one phrase: <c>string or null</c>.</summary>
    internal static string Describe(JsonTypes types) =>
        string.Join(" or ", Enum.GetValues<JsonTypes>().Where(type => type != JsonTypes.None && types.HasFlag(type)).Select(Name));

    private static JsonTypes JsonTypesOf(CelValueType type) => type switch
    {
        CelValueType.Bool => JsonTypes.Boolean,
        CelValueType.Int => JsonTypes.Integer | JsonTypes.Number,
        CelValueType.Decimal => JsonTypes.Number,
        CelValueType.String or CelValueType.Timestamp or CelValueType.Uuid or CelValueType.Duration => JsonTypes.String,
        CelValueType.StringList => JsonTypes.Array,
        CelValueType.Null => JsonTypes.Null,
        _ => JsonTypes.Object | JsonTypes.Array | JsonTypes.String | JsonTypes.Number | JsonTypes.Integer | JsonTypes.Boolean | JsonTypes.Null,
    };

    private static DescriptorValidationError Unknown(string path, string function, string name, IEnumerable<string> inputs)
    {
        var declared = inputs.Order(StringComparer.Ordinal).ToList();
        var closest = NameSuggestion.Closest(name, declared);

        return Error(
            $"{path}/input/{name}",
            $"'{name}' is not an input of function '{function}'.",
            closest is not null ? $"Did you mean '{closest}'?" : $"Its inputs are: {string.Join(", ", declared)}.");
    }

    private static DescriptorValidationError WrongType(string path, string function, string name, JsonTypes types, JsonValueKind kind) =>
        Error(
            $"{path}/input/{name}",
            $"'{name}' is {KindName(kind)}, and function '{function}' takes {Describe(types)} there.",
            $"Write a {Describe(types)} value, or a {{\"$cel\": \"…\"}} that yields one.");

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Name(JsonTypes type) => type.ToString().ToLowerInvariant();

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}
