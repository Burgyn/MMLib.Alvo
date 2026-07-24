using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// A value position that is either a JSON literal or a tagged CEL expression
/// <c>{"$cel": "..."}</c> (schema <c>$defs/valueOrExpr</c>). Exactly one of
/// <see cref="Literal"/> or <see cref="Expression"/> is set.
/// </summary>
[JsonConverter(typeof(ValueOrExprConverter))]
public sealed record ValueOrExpr
{
    /// <summary>The literal JSON value, when this is not a CEL expression.</summary>
    public JsonElement? Literal { get; init; }

    /// <summary>The CEL source from the <c>$cel</c> tag, when this is an expression.</summary>
    public string? Expression { get; init; }

    /// <summary><see langword="true"/> when this holds a tagged CEL expression rather than a literal.</summary>
    [JsonIgnore]
    public bool IsExpression => Expression is not null;

    /// <summary>Creates a value that carries the given JSON literal.</summary>
    /// <param name="literal">The literal JSON value.</param>
    public static ValueOrExpr FromLiteral(JsonElement literal) => new() { Literal = literal.Clone() };

    /// <summary>Creates a value that carries a tagged CEL expression.</summary>
    /// <param name="expression">The CEL source (without the <c>$cel</c> tag).</param>
    public static ValueOrExpr FromExpression(string expression) => new() { Expression = expression };
}

/// <summary>Serializes <see cref="ValueOrExpr"/> as a bare literal or a <c>{"$cel": "..."}</c> object.</summary>
internal sealed class ValueOrExprConverter : JsonConverter<ValueOrExpr>
{
    private const string CelProperty = "$cel";

    /// <inheritdoc />
    public override ValueOrExpr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement element = document.RootElement;

        return TryReadCel(element, out string? expression)
            ? ValueOrExpr.FromExpression(expression!)
            : ValueOrExpr.FromLiteral(element);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ValueOrExpr value, JsonSerializerOptions options)
    {
        if (value.IsExpression)
        {
            WriteCel(writer, value.Expression!);
            return;
        }

        value.Literal!.Value.WriteTo(writer);
    }

    private static bool TryReadCel(JsonElement element, out string? expression)
    {
        expression = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(CelProperty, out JsonElement cel)
            || cel.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        expression = cel.GetString();
        return true;
    }

    private static void WriteCel(Utf8JsonWriter writer, string expression)
    {
        writer.WriteStartObject();
        writer.WriteString(CelProperty, expression);
        writer.WriteEndObject();
    }
}
