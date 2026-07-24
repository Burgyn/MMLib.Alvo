using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// A flag that is either a static boolean or a CEL expression over <c>@user</c>
/// for a conditional (e.g. per-role) decision (schema <c>$defs/boolOrCel</c>).
/// Exactly one of <see cref="Boolean"/> or <see cref="Expression"/> is set.
/// </summary>
[JsonConverter(typeof(BoolOrCelConverter))]
public sealed record BoolOrCel
{
    /// <summary>The static boolean value, when this is not a CEL expression.</summary>
    public bool? Boolean { get; init; }

    /// <summary>The CEL source, when the decision is conditional.</summary>
    public string? Expression { get; init; }

    /// <summary><see langword="true"/> when this holds a CEL expression rather than a static boolean.</summary>
    [JsonIgnore]
    public bool IsExpression => Expression is not null;

    /// <summary>Creates a static boolean flag.</summary>
    /// <param name="value">The boolean value.</param>
    public static BoolOrCel FromBoolean(bool value) => new() { Boolean = value };

    /// <summary>Creates a conditional flag from a CEL expression.</summary>
    /// <param name="expression">The CEL source.</param>
    public static BoolOrCel FromExpression(string expression) => new() { Expression = expression };
}

/// <summary>Serializes <see cref="BoolOrCel"/> as a bare boolean or a CEL string.</summary>
internal sealed class BoolOrCelConverter : JsonConverter<BoolOrCel>
{
    /// <inheritdoc />
    public override BoolOrCel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True or JsonTokenType.False => BoolOrCel.FromBoolean(reader.GetBoolean()),
            JsonTokenType.String => BoolOrCel.FromExpression(reader.GetString()!),
            _ => throw new JsonException("Expected a boolean or a CEL string."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BoolOrCel value, JsonSerializerOptions options)
    {
        if (value.IsExpression)
        {
            writer.WriteStringValue(value.Expression);
            return;
        }

        writer.WriteBooleanValue(value.Boolean ?? false);
    }
}
