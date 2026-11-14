namespace MMLib.Alvo.Auth;

/// <summary>
/// The persisted registry of app tokens — the personal and service API keys a person mints for an integration (accounts
/// spec §7.1, S5 plan D2–D4). Implemented by the relational drivers; the core reaches it through
/// <c>InternalsVisibleTo</c> and routes every <c>alvo_pat_</c>/<c>alvo_svc_</c> key id here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, the <c>ITenantStore</c> precedent</b>: in-family producers and consumers only. Making it public later is
/// additive.
/// </para>
/// <para>
/// <b>Only the SHA-256 of the secret is ever stored</b>, and the store neither sees nor returns the secret itself. Every
/// instant and every id is passed in: the store reads no clock and mints no id. It stores what it is given — the name,
/// expiry and scope bounds (S5 plan D12, D13) are the core's.
/// </para>
/// </remarks>
internal interface IAppTokenStore
{
    /// <summary>
    /// The token <paramref name="keyId"/> names, in any state — revoked or expired included; whether it is usable is the
    /// resolver's judgement — or <see langword="null"/> when there is none, or when a stored scope no longer parses (a
    /// token is never trusted with fewer scopes than it was minted with, the <c>InMemoryApiKeyStore</c> rule).
    /// </summary>
    /// <param name="keyId">The key id.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct);

    /// <summary>Records <paramref name="usedAt"/> as the token's last use; throttling is the caller's (S5 plan D4).</summary>
    /// <param name="keyId">The key id.</param>
    /// <param name="usedAt">The instant of the use.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct);

    /// <summary>
    /// Mints a token in one transaction (S5 plan D10): the minter's account row is locked first, the live cap counted under
    /// that lock, and — for a token with a tenant — the tenant's <c>version</c> bumped before the minter's membership is
    /// read, so a mint serialises with every sweep that would revoke it. A refusal writes nothing.
    /// </summary>
    /// <param name="request">The token to mint, its minter, its bounds and the instant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct);

    /// <summary>
    /// The tokens <paramref name="person"/> sees, newest first and then by key id: their personal tokens, and the service
    /// tokens of every shared tenant they own — in every state, and unfiltered by the applied descriptor (S5 plan D14).
    /// </summary>
    /// <param name="person">The account listing.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct);

    /// <summary>
    /// Revokes a token — a personal one by its minter, a service one by its minter or any owner of its tenant. Idempotent: a
    /// second revocation keeps the first instant. An unknown token and one the actor may not revoke are one answer,
    /// <see cref="RevokeTokenStatus.NotFound"/>.
    /// </summary>
    /// <param name="revocation">The token, the actor and the instant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct);
}

/// <summary>One app-token mint, with every id, instant and bound chosen by the caller.</summary>
/// <param name="KeyId">The token's key id (<c>alvo_pat_…</c> or <c>alvo_svc_…</c>).</param>
/// <param name="Kind">The token's kind, <see cref="ApiKeyKind.Personal"/> or <see cref="ApiKeyKind.Service"/>.</param>
/// <param name="SecretHash">The SHA-256 of the secret, as <c>ApiKeyHash.Compute</c> gives it — never the secret.</param>
/// <param name="Name">The token's name.</param>
/// <param name="Minter">The account minting it.</param>
/// <param name="Actor">
/// The user the token acts as: the minter for a personal token, a fresh id for a service token (S5 plan D5).
/// </param>
/// <param name="Tenant">The tenant the token acts in; <see langword="null"/> only for a tenantless personal token.</param>
/// <param name="Roles">A service token's tenant roles, each held by the minter in <paramref name="Tenant"/>; empty for a personal one.</param>
/// <param name="Scopes">The token's scopes, in <see cref="ApiKeyScope.TryParse"/>'s form.</param>
/// <param name="ExpiresAt">When the token expires.</param>
/// <param name="MaxLive">How many live (unexpired, unrevoked) tokens one account may have minted (S5 plan D13).</param>
/// <param name="Now">The instant of the mint.</param>
internal sealed record MintTokenRequest(
    string KeyId,
    ApiKeyKind Kind,
    string SecretHash,
    string Name,
    UserId Minter,
    UserId Actor,
    TenantId? Tenant,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    int MaxLive,
    DateTimeOffset Now)
{
    /// <summary>Prints the request without its secret hash.</summary>
    /// <param name="builder">The builder to print into.</param>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("KeyId = ").Append(KeyId).Append(", Kind = ").Append(Kind).Append(", SecretHash = ***");
        builder.Append(", Minter = ").Append(Minter).Append(", Tenant = ").Append(Tenant);
        return true;
    }
}

/// <summary>What an app-token mint did.</summary>
internal enum MintTokenStatus
{
    /// <summary>The token was stored.</summary>
    Minted,

    /// <summary>The minter has no enabled account; nothing was written.</summary>
    NoAccount,

    /// <summary>No such tenant, or the minter is no member of it; nothing was written.</summary>
    NotFound,

    /// <summary>A service token's minter is not an owner of its tenant; nothing was written.</summary>
    NotOwner,

    /// <summary>A service token was asked for a personal tenant (S5 plan D8); nothing was written.</summary>
    PersonalTenant,

    /// <summary>A service token was asked for a role its minter does not hold in its tenant; nothing was written.</summary>
    RolesNotHeld,

    /// <summary>The minter already has <see cref="MintTokenRequest.MaxLive"/> live tokens; nothing was written.</summary>
    LimitReached,
}

/// <summary>A mint's status and, when it stored the token, the token's view.</summary>
/// <param name="Status">What the mint did.</param>
/// <param name="Token">The stored token, for <see cref="MintTokenStatus.Minted"/>.</param>
internal readonly record struct MintTokenOutcome(MintTokenStatus Status, AppTokenView? Token);

/// <summary>One app token as its owners see it — never its secret, never its hash.</summary>
/// <param name="KeyId">The key id.</param>
/// <param name="Kind">The token's kind.</param>
/// <param name="Name">The token's name.</param>
/// <param name="MintedBy">The account that minted it.</param>
/// <param name="MinterName">The minting account's display name, or <see langword="null"/>.</param>
/// <param name="Tenant">The tenant the token acts in, or <see langword="null"/>.</param>
/// <param name="Roles">A service token's stored tenant roles; empty for a personal one.</param>
/// <param name="Scopes">The token's scopes, as stored.</param>
/// <param name="CreatedAt">When the token was minted.</param>
/// <param name="ExpiresAt">When it expires.</param>
/// <param name="LastUsedAt">When it was last used, or <see langword="null"/>.</param>
/// <param name="RevokedAt">When it was revoked, or <see langword="null"/>.</param>
internal sealed record AppTokenView(
    string KeyId,
    ApiKeyKind Kind,
    string Name,
    UserId MintedBy,
    string? MinterName,
    TenantId? Tenant,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>One app-token revocation.</summary>
/// <param name="KeyId">The token's key id.</param>
/// <param name="Actor">The account revoking it.</param>
/// <param name="Now">The instant of the revocation.</param>
internal sealed record TokenRevocation(string KeyId, UserId Actor, DateTimeOffset Now);

/// <summary>What an app-token revocation did.</summary>
internal enum RevokeTokenStatus
{
    /// <summary>The token is revoked — now, or already before.</summary>
    Revoked,

    /// <summary>No such token, or one the actor may not revoke.</summary>
    NotFound,
}
