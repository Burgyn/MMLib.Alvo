using MMLib.Alvo.Auth;
using MMLib.Alvo.Tenants;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// An app-token registry holding what a fact puts in it and recording every read and touch — hand-written because
/// <see cref="IAppTokenStore"/> is internal to the Abstractions assembly, which a dynamic proxy cannot implement.
/// </summary>
internal sealed class RecordingAppTokenStore : IAppTokenStore
{
    private readonly Dictionary<string, ApiKeyRecord> _tokens = new(StringComparer.Ordinal);

    internal List<string> Finds { get; } = [];

    internal List<(string KeyId, DateTimeOffset UsedAt)> Touches { get; } = [];

    internal ApiKeyRecord Put(ApiKeyRecord token)
    {
        _tokens[token.KeyId] = token;
        return token;
    }

    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct)
    {
        Finds.Add(keyId);
        return ValueTask.FromResult(_tokens.GetValueOrDefault(keyId));
    }

    public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct)
    {
        Touches.Add((keyId, usedAt));
        return ValueTask.CompletedTask;
    }

    public Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct) => throw new NotSupportedException();

    public Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A tenant registry holding one person's memberships, recording every membership read.</summary>
internal sealed class RecordingMembershipStore : ITenantStore
{
    private readonly Dictionary<(UserId, TenantId), StoredMembership> _memberships = [];

    internal List<(UserId User, TenantId? Tenant)> Reads { get; } = [];

    internal void Set(UserId user, StoredMembership membership) => _memberships[(user, membership.Tenant)] = membership;

    public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct)
    {
        Reads.Add((user, tenant));
        return Task.FromResult(tenant is { } id ? _memberships.GetValueOrDefault((user, id)) : null);
    }

    public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) => throw new NotSupportedException();

    public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) => throw new NotSupportedException();

    public Task<InvitationPreview?> PreviewInvitationAsync(string codeHash, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<AcceptInvitationOutcome> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct) =>
        throw new NotSupportedException();
}
