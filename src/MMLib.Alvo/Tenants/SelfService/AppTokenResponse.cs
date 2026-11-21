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
/// <b>The token's list item, plus the credential and where to put it</b> (#542 item 1, S4 plan D11's shape for invitations):
/// every other member is exactly what <c>GET {prefix}/tokens</c> will show of it, from <see cref="AppTokenListItem.From"/>,
/// so a client parses one shape for a created and a listed token.
/// </para>
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
/// <param name="CreatedBy">The person who minted it.</param>
/// <param name="CreatedAt">When it was minted.</param>
/// <param name="ExpiresAt">When it expires.</param>
/// <param name="LastUsedAt">When it last authenticated a request; <see langword="null"/> for a token just minted.</param>
/// <param name="RevokedAt">When it was revoked; <see langword="null"/> for a token just minted.</param>
/// <param name="Status"><c>active</c>, as of the request, for a token just minted.</param>
internal sealed record AppTokenResponse(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("header")] string Header,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("createdBy")] PersonReference CreatedBy,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("status")] string Status)
{
    /// <summary>The <see cref="Kind"/> of a personal token.</summary>
    internal const string PersonalKind = "personal";

    /// <summary>The <see cref="Kind"/> of a service token.</summary>
    internal const string ServiceKind = "service";

    /// <summary>The response for the stored <paramref name="token"/>, carrying the credential <paramref name="presented"/>.</summary>
    /// <param name="token">The token as stored.</param>
    /// <param name="presented">The whole credential, <c>keyId.secret</c>.</param>
    /// <param name="header">The configured credential header.</param>
    /// <param name="now">The instant the request acts at, which its <see cref="Status"/> is judged at.</param>
    internal static AppTokenResponse From(AppTokenView token, string presented, string header, DateTimeOffset now)
    {
        var listed = AppTokenListItem.From(token, now);
        return new(
            listed.KeyId,
            presented,
            header,
            listed.Kind,
            listed.Name,
            listed.TenantId,
            listed.Roles,
            listed.Scopes,
            listed.CreatedBy,
            listed.CreatedAt,
            listed.ExpiresAt,
            listed.LastUsedAt,
            listed.RevokedAt,
            listed.Status);
    }

    /// <summary>Prints every member, the credential masked as <see cref="AppTokenSecret"/> prints itself.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> writes into.</param>
    private bool PrintMembers(StringBuilder builder)
    {
        var prefix = AppTokenSecret.KindOf(KeyId) == ApiKeyKind.Service ? AppTokenSecret.ServicePrefix : AppTokenSecret.PersonalPrefix;
        builder.Append("KeyId = ").Append(KeyId)
            .Append(", Token = ").Append(prefix).Append("***")
            .Append(", Kind = ").Append(Kind)
            .Append(", Header = ").Append(Header)
            .Append(", Name = ").Append(Name)
            .Append(", TenantId = ").Append(TenantId)
            .Append(", Roles = [").AppendJoin(", ", Roles).Append(']')
            .Append(", Scopes = [").AppendJoin(", ", Scopes).Append(']')
            .Append(", CreatedBy = ").Append(CreatedBy)
            .Append(", CreatedAt = ").Append(CreatedAt)
            .Append(", ExpiresAt = ").Append(ExpiresAt)
            .Append(", Status = ").Append(Status);
        return true;
    }
}
