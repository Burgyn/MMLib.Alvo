using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Text;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A just-minted app token, answered by <c>POST {prefix}/tokens</c> (S5 plan D14): the one answer that carries
/// <see cref="Token"/>, the credential itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The secret is shown once.</b> The registry holds only its hash, so no later answer can repeat it. <see cref="PrintMembers"/>
/// masks it as <c>alvo_pat_***</c> or <c>alvo_svc_***</c>, so neither a log line nor a debugger string carries it.
/// </para>
/// <para>
/// <b>Where to put it is part of the answer</b>: <see cref="Header"/> is the configured credential header, so an agent never
/// guesses it. Member names are fixed with <see cref="JsonPropertyNameAttribute"/> (the <see cref="InvitationResponse"/>
/// precedent).
/// </para>
/// </remarks>
/// <param name="KeyId">The token's key id — not secret, the one listing and revoking take.</param>
/// <param name="Token">The whole credential, <c>keyId.secret</c>.</param>
/// <param name="Header">The request header to present it in.</param>
/// <param name="Kind"><c>personal</c> or <c>service</c>.</param>
/// <param name="Name">The token's name.</param>
/// <param name="TenantId">The tenant it acts in, or <see langword="null"/> for a tenantless personal token.</param>
/// <param name="Roles">A service token's tenant roles; empty for a personal one.</param>
/// <param name="Scopes">What it may read and write.</param>
/// <param name="ExpiresAt">When it expires.</param>
/// <param name="CreatedAt">When it was minted.</param>
internal sealed record TokenResponse(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("header")] string Header,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt)
{
    /// <summary>The <see cref="Kind"/> of a personal token.</summary>
    internal const string PersonalKind = "personal";

    /// <summary>The <see cref="Kind"/> of a service token.</summary>
    internal const string ServiceKind = "service";

    /// <summary>The response for the stored <paramref name="token"/>, carrying the credential <paramref name="presented"/>.</summary>
    /// <param name="token">The token as stored.</param>
    /// <param name="presented">The whole credential, <c>keyId.secret</c>.</param>
    /// <param name="header">The configured credential header.</param>
    internal static TokenResponse From(AppTokenView token, string presented, string header)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new(
            token.KeyId,
            presented,
            header,
            token.Kind == ApiKeyKind.Service ? ServiceKind : PersonalKind,
            token.Name,
            token.Tenant?.Value,
            token.Roles,
            token.Scopes,
            token.ExpiresAt,
            token.CreatedAt);
    }

    /// <summary>Prints every member, the credential masked as <see cref="AppTokenSecret"/> prints itself.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> writes into.</param>
    private bool PrintMembers(StringBuilder builder)
    {
        var prefix = AppTokenSecret.KindOf(KeyId) == ApiKeyKind.Service ? AppTokenSecret.ServicePrefix : AppTokenSecret.PersonalPrefix;
        builder.Append("KeyId = ").Append(KeyId)
            .Append(", Token = ").Append(prefix).Append("***")
            .Append(", Kind = ").Append(Kind)
            .Append(", Name = ").Append(Name)
            .Append(", TenantId = ").Append(TenantId)
            .Append(", Roles = [").AppendJoin(", ", Roles).Append(']')
            .Append(", Scopes = [").AppendJoin(", ", Scopes).Append(']')
            .Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}
