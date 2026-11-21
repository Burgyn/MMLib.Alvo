using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A credential write whose session ends while the request is in flight writes nothing (stack review A-I1, round 2). A
/// thief holding a stolen session can end it on purpose — by presenting the refresh token its own refresh spent — after
/// the route admitted the request and before the store opens its transaction (a slow body holds that gap open). The
/// decorated store ends the caller's session exactly there, as that reuse would, and the route must answer the dead
/// session's own <c>401 invalid_token</c> and leave no token and no invitation behind. The store-level races are
/// <c>AccountStoreFacts</c>'.
/// </summary>
public sealed class CredentialWritesMidReuseTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_mint_whose_session_ends_mid_request_is_401_and_stores_nothing()
    {
        var ender = new SessionEnder();
        await using var world = await AccountsWorld.StartAsync(
            _engine, descriptor: TeamTracker, services: services => services.Decorate<IAppTokenStore>(inner => new EndingTokenStore(inner, ender)));
        var (ada, _) = await world.SignInAsync("ada");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        var adaId = await world.UserIdAsync(ada);
        ender.Accounts = world.Api.Services.GetRequiredService<IAccountStore>();

        using var minted = await world.AsPersonAsync(HttpMethod.Post, "/auth/v1/tokens", ada, ServiceToken(world, tenant));

        ender.Seen.ShouldNotBeNull("the route carries the caller's session into the mint");
        minted.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await minted.ReadTextAsync());
        minted.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        (await world.Api.Services.GetRequiredService<IAppTokenStore>().ListAsync(adaId, TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invitation_whose_session_ends_mid_request_is_401_and_stores_nothing()
    {
        var ender = new SessionEnder();
        await using var world = await AccountsWorld.StartAsync(
            _engine, descriptor: TeamTracker, services: services => services.Decorate<ITenantStore>(inner => new EndingTenantStore(inner, ender)));
        var (ada, _) = await world.SignInAsync("ada");
        var tenant = await TenantRoutesTests.CreatedAsync(world, ada, "Acme");
        ender.Accounts = world.Api.Services.GetRequiredService<IAccountStore>();

        using var invited = await world.AsPersonAsync(
            HttpMethod.Post, InvitationRoutesTests.Path(tenant), ada, new JsonObject { ["roles"] = new JsonArray("technician"), ["isOwner"] = true });

        ender.Seen.ShouldNotBeNull("the route carries the caller's session into the creation");
        invited.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await invited.ReadTextAsync());
        invited.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer error=\"invalid_token\"");
        (await world.Api.Services.GetRequiredService<ITenantStore>().ListInvitationsAsync(new TenantId(tenant), TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    private static JsonObject ServiceToken(AccountsWorld world, Guid tenant) => new()
    {
        ["name"] = "billing",
        ["kind"] = "service",
        ["tenantId"] = tenant.ToString("D"),
        ["roles"] = new JsonArray("dispatcher"),
        ["scopes"] = new JsonArray("jobs:read"),
        ["expiresAt"] = world.Clock.GetUtcNow().AddDays(7).ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>Ends the session a credential write names, as a reuse committing between the route's check and the store would.</summary>
    private sealed class SessionEnder
    {
        internal IAccountStore? Accounts { get; set; }

        internal Guid? Seen { get; private set; }

        internal async Task EndAsync(Guid? session, DateTimeOffset now)
        {
            Seen = session;
            if (session is { } id && Accounts is { } accounts)
            {
                await accounts.RevokeSessionAsync(id, SessionRevocations.Reuse, now, TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class EndingTokenStore(IAppTokenStore inner, SessionEnder ender) : IAppTokenStore
    {
        public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct) => inner.FindAsync(keyId, ct);

        public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct) => inner.TouchAsync(keyId, usedAt, ct);

        public async Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct)
        {
            await ender.EndAsync(request.Session, request.Now);
            return await inner.MintAsync(request, ct);
        }

        public Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct) => inner.ListAsync(person, ct);

        public Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct) => inner.RevokeAsync(revocation, ct);
    }

    private sealed class EndingTenantStore(ITenantStore inner, SessionEnder ender) : ITenantStore
    {
        public async Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct)
        {
            await ender.EndAsync(request.Session, request.Now);
            return await inner.CreateInvitationAsync(request, ct);
        }

        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) => inner.CreateSharedAsync(request, ct);

        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) => inner.ListMembershipsAsync(user, ct);

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct) =>
            inner.FindMembershipAsync(user, tenant, ct);

        public Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) => inner.ListTenantsAsync(ct);

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            inner.RenameAsync(tenant, actor, name, ct);

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            inner.ListMembersAsync(tenant, actor, ct);

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) => inner.UpdateMemberAsync(update, ct);

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) => inner.RemoveMemberAsync(removal, ct);

        public Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
            inner.ListInvitationsAsync(tenant, ct);

        public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct) =>
            inner.RevokeInvitationAsync(revocation, ct);

        public Task<InvitationPreview?> PreviewInvitationAsync(string codeHash, DateTimeOffset now, CancellationToken ct) =>
            inner.PreviewInvitationAsync(codeHash, now, ct);

        public Task<AcceptInvitationOutcome> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct) =>
            inner.AcceptInvitationAsync(request, ct);

        public Task<OwnershipGrantOutcome> GrantOwnershipAsync(OwnershipGrant grant, CancellationToken ct) =>
            inner.GrantOwnershipAsync(grant, ct);

        public Task<IReadOnlyList<OrphanedTenant>> ListOrphanedTenantsAsync(UserId account, CancellationToken ct) =>
            inner.ListOrphanedTenantsAsync(account, ct);
    }
}
