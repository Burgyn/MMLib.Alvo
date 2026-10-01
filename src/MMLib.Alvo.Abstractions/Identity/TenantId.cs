using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo;

/// <summary>
/// The identifier of the tenant a caller acts in. A <see langword="null"/>
/// <see cref="TenantId"/> on a tenant-scoped entity denies rather than widening to every
/// tenant — the absence of a tenant is never treated as "all tenants".
/// </summary>
/// <remarks>
/// <para>
/// <b>The all-zero uuid (<see langword="default"/>, <see cref="Guid.Empty"/>) is reserved to mean
/// "no tenant" and is never a tenant a caller may act in.</b> <see cref="UserId"/> reserves the
/// same value for the same reason, and on this type the consequence is worse: a
/// <see cref="TenantId"/> that is present but all-zero reads to every layer above as <i>a caller
/// who holds a tenant</i>, so the tenant predicate is attached and compares equal to every row
/// whose <c>tenant_id</c> was defaulted rather than assigned — which a partially migrated dataset
/// really does contain. The absence of a tenant must therefore be expressed as
/// <see langword="null"/>, never as all-zero: <see langword="null"/> denies, all-zero would match.
/// </para>
/// <para>
/// <b>Where that is enforced.</b> Every path that mints a tenant from untrusted text refuses the
/// reserved value — <see cref="Parse"/>, <see cref="TryParse"/> and the JSON converter below — and
/// a tenant grant arriving as an already-parsed <c>uuid</c> is refused one layer further in, by the
/// guard every <c>IAlvoUserAdministration</c> implementation is reached through. The bare
/// constructor is left permissive on purpose: <see langword="default"/> bypasses any constructor a
/// struct could declare, so a throwing constructor would buy the appearance of a guarantee without
/// the guarantee.
/// </para>
/// </remarks>
/// <param name="Value">The underlying identifier.</param>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(Guid Value) : IParsable<TenantId>
{
    /// <summary>Creates a new, random identifier.</summary>
    public static TenantId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();

    /// <inheritdoc />
    /// <exception cref="FormatException">
    /// The text is not a uuid, or is the reserved all-zero uuid.
    /// </exception>
    public static TenantId Parse(string s, IFormatProvider? provider)
        => TryParse(s, provider, out var result)
            ? result
            : throw new FormatException(
                "A tenant id must be a uuid other than the reserved all-zero value, which means "
                + "\"no tenant\".");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TenantId result)
    {
        if (Guid.TryParse(s, out var value) && value != Guid.Empty)
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
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a UUID string for a tenant id.");
        }

        var value = reader.GetGuid();
        return value == Guid.Empty
            ? throw new JsonException(
                "The all-zero uuid is reserved to mean \"no tenant\" and is not a tenant id. Send "
                + "null to express the absence of a tenant.")
            : new TenantId(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
