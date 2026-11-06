using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// The input of a <c>function</c> action: an object of input property to literal value or tagged CEL
/// expression, or a JSONata transformation string. Exactly one of <see cref="Transform"/> or
/// <see cref="Fields"/> is set.
/// </summary>
[JsonConverter(typeof(FunctionInputConverter))]
public sealed record FunctionInput
{
    /// <summary>The JSONata transformation, when the input is written as a string.</summary>
    public string? Transform { get; init; }

    /// <summary>The object form: input property to literal value or tagged CEL expression.</summary>
    public IReadOnlyDictionary<string, ValueOrExpr?>? Fields { get; init; }
}

/// <summary>Serializes <see cref="FunctionInput"/> as a JSONata string or an object of values.</summary>
internal sealed class FunctionInputConverter : JsonConverter<FunctionInput>
{
    private static readonly ValueOrExprConverter _values = new();

    /// <inheritdoc />
    public override FunctionInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => new FunctionInput { Transform = reader.GetString() },
            JsonTokenType.StartObject => new FunctionInput { Fields = ReadFields(ref reader, options) },
            _ => throw new JsonException(
                $"A function input is an object of values or a transformation string, not a JSON {reader.TokenType}."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FunctionInput value, JsonSerializerOptions options)
    {
        if (value.Fields is null)
        {
            writer.WriteStringValue(value.Transform);
            return;
        }

        writer.WriteStartObject();
        foreach (var (name, field) in value.Fields)
        {
            writer.WritePropertyName(name);
            _values.Write(writer, field ?? NullLiteral(), options);
        }

        writer.WriteEndObject();
    }

    private static Dictionary<string, ValueOrExpr?> ReadFields(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var fields = new Dictionary<string, ValueOrExpr?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            fields[name] = _values.Read(ref reader, typeof(ValueOrExpr), options);
        }

        return fields;
    }

    private static ValueOrExpr NullLiteral() => ValueOrExpr.FromLiteral(JsonElement.Parse("null"));
}
