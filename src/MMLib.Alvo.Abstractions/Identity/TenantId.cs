using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo;

/// <summary>
/// The identifier of the tenant a caller acts in. A <see langword="null"/>
/// <see cref="TenantId"/> on a tenant-scoped entity denies rather than widening to every
/// tenant — the absence of a tenant is never treated as "all tenants".
/// </summary>
/// <param name="Value">The underlying identifier.</param>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(Guid Value) : IParsable<TenantId>
{
    /// <summary>Creates a new, random identifier.</summary>
    public static TenantId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();

    /// <inheritdoc />
    public static TenantId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TenantId result)
    {
        if (Guid.TryParse(s, out var value))
        {
            result = new TenantId(value);
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>Serializes <see cref="TenantId"/> as a bare JSON string.</summary>
internal sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    /// <inheritdoc />
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? new TenantId(reader.GetGuid())
            : throw new JsonException("Expected a UUID string for a tenant id.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
