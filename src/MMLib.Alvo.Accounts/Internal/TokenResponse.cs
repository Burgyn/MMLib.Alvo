using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The token endpoint's successful answer: RFC 6749 §5.1's members, plus RFC 8693 §2.2.1's <c>issued_token_type</c> on an
/// exchange (spec §5.2 step 7).
/// </summary>
/// <param name="AccessToken">The access token; never printed.</param>
/// <param name="IssuedTokenType">
/// <c>urn:ietf:params:oauth:token-type:access_token</c> on an exchange; <see langword="null"/>, and so absent, on a refresh,
/// which RFC 6749 §6 answers without it.
/// </param>
/// <param name="TokenType">Always <c>Bearer</c> (RFC 6750).</param>
/// <param name="ExpiresIn">The access token's lifetime, in seconds.</param>
/// <param name="RefreshToken">The refresh token; never printed.</param>
internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("issued_token_type")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IssuedTokenType,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken)
{
    /// <summary>The <c>token_type</c> of every access token Alvo issues.</summary>
    internal const string Bearer = "Bearer";

    /// <summary>
    /// Serialized with its own options, never the host's: the member names are RFC 6749's, and a host naming policy or
    /// ignore condition must not be able to rename or drop one.
    /// </summary>
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    /// <summary>The 200 that carries this response.</summary>
    internal IResult Ok() => Results.Json(this, _json, contentType: "application/json", statusCode: StatusCodes.Status200OK);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessToken = ***");
        builder.Append(", IssuedTokenType = ").Append(IssuedTokenType);
        builder.Append(", TokenType = ").Append(TokenType);
        builder.Append(", ExpiresIn = ").Append(ExpiresIn);
        builder.Append(", RefreshToken = ***");
        return true;
    }
}
