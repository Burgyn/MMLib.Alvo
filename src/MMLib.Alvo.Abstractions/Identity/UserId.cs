using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo;

/// <summary>
/// The internal identifier of a caller. An external subject (an OIDC <c>sub</c>, an API key
/// identifier) is mapped to a <see cref="UserId"/>; the raw external value is never stored in
/// a record, so the framework-managed <c>created_by</c> / <c>updated_by</c> columns stay
/// <c>uuid</c>.
/// </summary>
/// <remarks>
/// <b>The all-zero uuid (<see langword="default"/>, <see cref="Guid.Empty"/>) is reserved to mean "no
/// identity" and must never be minted for a real caller.</b> <see cref="AlvoContext.Anonymous"/>
/// carries exactly that value, and the rule engine reads it as the absence of an identity rather than
/// as a caller: an operation whose policy reads <c>@user.id</c> is denied outright for such a caller,
/// instead of resolving the comparison against the all-zero uuid and thereby making the anonymous
/// caller the "owner" of every row whose owner column is all-zero — which a partially-migrated or
/// defaulted dataset really does contain. A host mapping an external subject (an OIDC <c>sub</c>, an
/// API key identifier) onto a <see cref="UserId"/> must therefore never map it onto the all-zero value.
/// </remarks>
/// <param name="Value">The underlying identifier.</param>
[JsonConverter(typeof(UserIdJsonConverter))]
public readonly record struct UserId(Guid Value) : IParsable<UserId>
{
    /// <summary>Creates a new, random identifier.</summary>
    public static UserId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();

    /// <inheritdoc />
    public static UserId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out UserId result)
    {
        if (Guid.TryParse(s, out var value))
        {
            result = new UserId(value);
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>Serializes <see cref="UserId"/> as a bare JSON string.</summary>
internal sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    /// <inheritdoc />
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? new UserId(reader.GetGuid())
            : throw new JsonException("Expected a UUID string for a user id.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
