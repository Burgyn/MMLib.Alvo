using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A minted personal or service token as a client holds it: its key id and the whole credential.</summary>
/// <param name="KeyId">The token's key id, <c>alvo_pat_…</c> or <c>alvo_svc_…</c>.</param>
/// <param name="Presented">The credential a client sends as <c>X-Alvo-Api-Key</c>.</param>
internal sealed record MintedToken(string KeyId, string Presented);

/// <summary>Mints app tokens straight into a <see cref="FakeAppTokenStore"/>, the way the registry would hold them.</summary>
internal static class AppTokenFakes
{
    /// <summary>A store holding one live token of <paramref name="kind"/> with <paramref name="scopes"/>.</summary>
    /// <param name="kind"><see cref="ApiKeyKind.Personal"/> (tenantless) or <see cref="ApiKeyKind.Service"/> (in <paramref name="tenant"/>, as a dispatcher).</param>
    /// <param name="tenant">The tenant a service token acts in.</param>
    /// <param name="scopes">The token's <c>entity:read|write</c> scopes.</param>
    internal static (FakeAppTokenStore Store, MintedToken Token) StoreWith(ApiKeyKind kind, Guid tenant, params string[] scopes)
    {
        var store = new FakeAppTokenStore();
        return (store, store.Mint(kind, tenant, scopes));
    }

    /// <summary>Presents <paramref name="token"/> on <paramref name="world"/>'s credential header.</summary>
    /// <param name="world">The world the request goes to.</param>
    /// <param name="token">The token to present.</param>
    internal static KeyValuePair<string, string>[] Presenting(AlvoApiWorld world, MintedToken token) =>
        [new(world.CredentialHeaderName, token.Presented)];

    internal static ApiKeyScope Scope(string text) =>
        ApiKeyScope.TryParse(text, out var scope) ? scope : throw new InvalidOperationException($"{text} must parse");
}

/// <summary>An in-memory <see cref="IAppTokenStore"/> that resolves the tokens put in it and mints nothing over HTTP.</summary>
internal sealed class FakeAppTokenStore : IAppTokenStore
{
    private readonly Dictionary<string, ApiKeyRecord> _tokens = new(StringComparer.Ordinal);

    /// <summary>Puts a fresh, live token of <paramref name="kind"/> into the store.</summary>
    /// <param name="kind"><see cref="ApiKeyKind.Personal"/> (tenantless) or <see cref="ApiKeyKind.Service"/> (in <paramref name="tenant"/>, as a dispatcher).</param>
    /// <param name="tenant">The tenant a service token acts in.</param>
    /// <param name="scopes">The token's scopes.</param>
    internal MintedToken Mint(ApiKeyKind kind, Guid tenant, params string[] scopes)
    {
        var secret = AppTokenSecret.New(kind);
        _tokens[secret.KeyId] = new ApiKeyRecord
        {
            KeyId = secret.KeyId,
            Sha256Hash = secret.Hash,
            User = new UserId(Guid.NewGuid()),
            RoleNames = kind == ApiKeyKind.Service ? ["dispatcher"] : [],
            Tenant = kind == ApiKeyKind.Service ? new TenantId(tenant) : null,
            Scopes = scopes.Select(AppTokenFakes.Scope).ToHashSet(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            Kind = kind,
            MintedBy = new UserId(Guid.NewGuid()),
        };
        return new MintedToken(secret.KeyId, secret.Presented);
    }

    internal void Expire(string keyId) => _tokens[keyId] = _tokens[keyId] with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };

    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct) =>
        ValueTask.FromResult(_tokens.GetValueOrDefault(keyId));

    public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct) => ValueTask.CompletedTask;

    public Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct) => throw new NotSupportedException();

    public Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct) => throw new NotSupportedException();
}
