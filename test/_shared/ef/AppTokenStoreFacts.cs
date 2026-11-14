using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The app-token registry — minting under its locks, finding a token as an API-key record, touching, listing and revoking —
/// the same facts on SQLite and on PostgreSQL (accounts spec §7.1; S5 plan D3, D4, D10, D13, D14). Accounts are created
/// through <see cref="IAccountStore.SignInAsync"/>, tenants through <see cref="ITenantStore.CreateSharedAsync"/>, and
/// members through <see cref="TenantSeed"/>.
/// </summary>
public abstract class AppTokenStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Gets the running fact's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts one fresh database and returns its host container.</summary>
    protected abstract Task<IServiceProvider> StartAsync();

    /// <summary>Disposes the engine fixture.</summary>
    protected abstract ValueTask DisposeFixtureAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisposeFixtureAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_personal_token_round_trips_as_an_api_key_record()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(services);
        var secret = AppTokenSecret.New(ApiKeyKind.Personal);
        var request = Personal(owner, tenant) with
        {
            KeyId = secret.KeyId,
            SecretHash = secret.Hash,
            Scopes = ["orders:read", "customers:write"],
        };

        (await store.MintAsync(request, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        var record = (await store.FindAsync(secret.KeyId, Ct)).ShouldNotBeNull();

        (record.KeyId, record.Kind, record.Sha256Hash, record.User, record.MintedBy, record.Tenant)
            .ShouldBe((secret.KeyId, ApiKeyKind.Personal, secret.Hash, owner, (UserId?)owner, (TenantId?)tenant));
        record.RoleNames.ShouldBeEmpty();
        record.Scopes.ShouldBe(
            [new ApiKeyScope { Entity = "orders", Access = ScopeAccess.Read }, new ApiKeyScope { Entity = "customers", Access = ScopeAccess.Write }],
            ignoreOrder: true);
        (record.ExpiresAt, record.RevokedAt, record.LastUsedAt).ShouldBe(((DateTimeOffset?)_now.AddDays(30), (DateTimeOffset?)null, (DateTimeOffset?)null));
        (await RowTextAsync(services, secret.KeyId)).ShouldNotContain(secret.Presented[(secret.KeyId.Length + 1)..]);
        request.ToString().ShouldNotContain(secret.Hash);
        (await store.FindAsync("alvo_pat_unknown", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_token_with_a_scope_that_no_longer_parses_is_not_found()
    {
        var (store, services) = await StoreAsync();
        var request = Personal(await SignedUpAsync(services), tenant: null);
        await store.MintAsync(request, Ct);

        await ScalarAsync(services, $"UPDATE {ApiKeys(services)} SET scopes = '[\"orders:read\",\"orders:delete\"]' WHERE key_id = '{request.KeyId}'");

        (await store.FindAsync(request.KeyId, Ct)).ShouldBeNull("never trusted with fewer scopes than it was minted with");
    }

    [Fact]
    public async Task A_tenantless_personal_token_needs_only_an_enabled_account()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services, "Ada Lovelace");
        var request = Personal(person, tenant: null);

        var outcome = await store.MintAsync(request, Ct);

        outcome.Status.ShouldBe(MintTokenStatus.Minted);
        Same(outcome.Token.ShouldNotBeNull(), new AppTokenView(
            request.KeyId, ApiKeyKind.Personal, "ci", person, "Ada Lovelace", null, [], ["orders:read"], _now, _now.AddDays(30), null, null));
        (await store.FindAsync(request.KeyId, Ct)).ShouldNotBeNull().Tenant.ShouldBeNull();
    }

    [Fact]
    public async Task A_personal_token_for_a_tenant_needs_a_membership_of_it()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var stranger = await SignedUpAsync(services);

        (await store.MintAsync(Personal(stranger, tenant), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.NotFound, null));
        (await store.MintAsync(Personal(owner, TenantId.New()), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.NotFound, null));
        (await CountAsync(services)).ShouldBe(0);
        (await VersionAsync(services, tenant)).ShouldBe(0, "a refusal rolls its version bump back");

        var minted = await store.MintAsync(Personal(member, tenant), Ct);

        minted.Status.ShouldBe(MintTokenStatus.Minted, "a personal token needs a membership, not ownership");
        minted.Token.ShouldNotBeNull().Tenant.ShouldBe(tenant);
        (await VersionAsync(services, tenant)).ShouldBe(1, "a mint into a tenant takes the tenant's lock");
    }

    [Fact]
    public async Task A_service_token_needs_an_owner_of_a_shared_tenant()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);

        (await store.MintAsync(Service(member, tenant, ["viewer"]), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.NotOwner, null));
        (await store.MintAsync(Service(owner, personal, []), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.PersonalTenant, null));
        (await CountAsync(services)).ShouldBe(0);

        var request = Service(owner, tenant, ["dispatcher"]);
        var outcome = await store.MintAsync(request, Ct);

        outcome.Status.ShouldBe(MintTokenStatus.Minted);
        Same(outcome.Token.ShouldNotBeNull(), new AppTokenView(
            request.KeyId, ApiKeyKind.Service, "ci", owner, "Ada", tenant, ["dispatcher"], ["orders:read"], _now, _now.AddDays(30), null, null));
        var record = (await store.FindAsync(request.KeyId, Ct)).ShouldNotBeNull();
        (record.Kind, record.User, record.MintedBy, record.Tenant).ShouldBe((ApiKeyKind.Service, request.Actor, (UserId?)owner, (TenantId?)tenant));
        record.User.ShouldNotBe(owner, "a service token acts as its own user, never as its minter");
        record.RoleNames.ShouldBe(["dispatcher"]);
    }

    [Fact]
    public async Task A_service_token_carries_only_roles_its_minter_holds()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(services);

        (await store.MintAsync(Service(owner, tenant, ["dispatcher", "admin"]), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.RolesNotHeld, null));
        (await store.MintAsync(Service(owner, tenant, ["Dispatcher"]), Ct)).Status.ShouldBe(MintTokenStatus.RolesNotHeld, "role names are ordinal");
        (await CountAsync(services)).ShouldBe(0);

        (await store.MintAsync(Service(owner, tenant, ["planner", "dispatcher"]), Ct)).Status.ShouldBe(MintTokenStatus.Minted);
    }

    [Fact]
    public async Task A_disabled_or_unknown_minter_mints_nothing()
    {
        var (store, services) = await StoreAsync();
        var disabled = await SignedUpAsync(services);
        (await services.GetRequiredService<IAccountStore>().SetDisabledAsync(disabled, disabled: true, _now, Ct)).ShouldBeTrue();

        (await store.MintAsync(Personal(disabled, tenant: null), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.NoAccount, null));
        (await store.MintAsync(Personal(UserId.New(), tenant: null), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.NoAccount, null));

        (await CountAsync(services)).ShouldBe(0);
    }

    [Fact]
    public async Task Only_a_personal_token_or_a_service_token_with_a_tenant_is_minted()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);

        await Should.ThrowAsync<ArgumentException>(async () => await store.MintAsync(Personal(person, null) with { Kind = ApiKeyKind.Configured }, Ct));
        await Should.ThrowAsync<ArgumentException>(async () => await store.MintAsync(Personal(person, null) with { Kind = ApiKeyKind.Service }, Ct));
        await Should.ThrowAsync<ArgumentNullException>(async () => await store.MintAsync(null!, Ct));
    }

    [Fact]
    public async Task The_live_cap_counts_only_unexpired_unrevoked_tokens()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var lasting = Personal(person, null, maxLive: 2);
        var brief = Personal(person, null, maxLive: 2) with { ExpiresAt = _now.AddHours(1) };
        (await store.MintAsync(lasting, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        (await store.MintAsync(brief, Ct)).Status.ShouldBe(MintTokenStatus.Minted);

        (await store.MintAsync(Personal(person, null, maxLive: 2), Ct)).ShouldBe(new MintTokenOutcome(MintTokenStatus.LimitReached, null));
        (await store.MintAsync(Personal(other, null, maxLive: 2), Ct)).Status.ShouldBe(MintTokenStatus.Minted, "the cap is per minter");

        (await store.RevokeAsync(new TokenRevocation(lasting.KeyId, person, _now), Ct)).ShouldBe(RevokeTokenStatus.Revoked);
        (await store.MintAsync(Personal(person, null, maxLive: 2), Ct)).Status.ShouldBe(MintTokenStatus.Minted, "a revoked token frees a slot");
        (await store.MintAsync(Personal(person, null, maxLive: 2), Ct)).Status.ShouldBe(MintTokenStatus.LimitReached);

        var later = Personal(person, null, maxLive: 2, now: _now.AddHours(1));
        (await store.MintAsync(later, Ct)).Status.ShouldBe(MintTokenStatus.Minted, "a token expiring at the instant frees a slot");
        (await CountAsync(services)).ShouldBe(5);
    }

    [Fact]
    public async Task The_list_holds_my_personal_tokens_and_service_tokens_of_tenants_I_own()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var (other, otherOwner, _) = await TeamAsync(services);
        var coOwner = await SignedUpAsync(services, "Grace");
        await TenantSeed.AddMemberAsync(services, tenant, coOwner, [], isOwner: true, _now);
        var mine = Personal(owner, null, now: _now.AddMinutes(1));
        var service = Service(owner, tenant, ["dispatcher"], now: _now.AddMinutes(2));
        var tiedLow = Personal(owner, tenant, now: _now.AddMinutes(3)) with { KeyId = "alvo_pat_A" };
        var tiedHigh = Personal(owner, tenant, now: _now.AddMinutes(3)) with { KeyId = "alvo_pat_B" };
        var members = Personal(member, tenant, now: _now.AddMinutes(4));
        var elsewhere = Service(otherOwner, other, ["dispatcher"], now: _now.AddMinutes(5));
        foreach (var request in new[] { tiedHigh, mine, service, members, elsewhere, tiedLow })
        {
            (await store.MintAsync(request, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        }

        (await store.RevokeAsync(new TokenRevocation(mine.KeyId, owner, _now.AddHours(1)), Ct)).ShouldBe(RevokeTokenStatus.Revoked);

        (await KeysAsync(store, owner)).ShouldBe([tiedLow.KeyId, tiedHigh.KeyId, service.KeyId, mine.KeyId]);
        (await KeysAsync(store, member)).ShouldBe([members.KeyId], "a member who is no owner sees no service token");
        var seen = (await store.ListAsync(coOwner, Ct)).ShouldHaveSingleItem();
        (seen.KeyId, seen.MintedBy, seen.MinterName).ShouldBe((service.KeyId, owner, "Ada"));
        (await store.ListAsync(owner, Ct)).Single(view => view.KeyId == mine.KeyId).RevokedAt.ShouldBe(_now.AddHours(1));
    }

    [Fact]
    public async Task Revoking_is_the_minter_s_or_any_owner_s_and_idempotent()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var coOwner = await SignedUpAsync(services);
        await TenantSeed.AddMemberAsync(services, tenant, coOwner, [], isOwner: true, _now);
        var stranger = await SignedUpAsync(services);
        var personal = Personal(member, tenant);
        var service = Service(owner, tenant, ["dispatcher"]);
        await store.MintAsync(personal, Ct);
        await store.MintAsync(service, Ct);

        foreach (var actor in new[] { owner, coOwner, stranger })
        {
            (await store.RevokeAsync(new TokenRevocation(personal.KeyId, actor, _now), Ct)).ShouldBe(RevokeTokenStatus.NotFound);
        }

        foreach (var actor in new[] { member, stranger })
        {
            (await store.RevokeAsync(new TokenRevocation(service.KeyId, actor, _now), Ct)).ShouldBe(RevokeTokenStatus.NotFound);
        }

        (await store.RevokeAsync(new TokenRevocation("alvo_pat_unknown", owner, _now), Ct)).ShouldBe(RevokeTokenStatus.NotFound);
        (await RevokedAtAsync(store, personal.KeyId)).ShouldBeNull();
        (await RevokedAtAsync(store, service.KeyId)).ShouldBeNull();

        (await store.RevokeAsync(new TokenRevocation(personal.KeyId, member, _now.AddHours(1)), Ct)).ShouldBe(RevokeTokenStatus.Revoked);
        (await store.RevokeAsync(new TokenRevocation(personal.KeyId, member, _now.AddHours(2)), Ct)).ShouldBe(RevokeTokenStatus.Revoked);
        (await store.RevokeAsync(new TokenRevocation(service.KeyId, coOwner, _now.AddHours(1)), Ct)).ShouldBe(RevokeTokenStatus.Revoked);
        (await store.RevokeAsync(new TokenRevocation(service.KeyId, owner, _now.AddHours(2)), Ct)).ShouldBe(RevokeTokenStatus.Revoked);

        (await RevokedAtAsync(store, personal.KeyId)).ShouldBe(_now.AddHours(1), "the second revocation keeps the first instant");
        (await RevokedAtAsync(store, service.KeyId)).ShouldBe(_now.AddHours(1));
    }

    [Fact]
    public async Task A_service_token_s_minter_revokes_it_and_an_owner_of_another_tenant_does_not()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(services);
        var (_, otherOwner, _) = await TeamAsync(services);
        var service = Service(owner, tenant, ["dispatcher"]);
        await store.MintAsync(service, Ct);

        (await store.RevokeAsync(new TokenRevocation(service.KeyId, otherOwner, _now), Ct)).ShouldBe(RevokeTokenStatus.NotFound);
        (await store.RevokeAsync(new TokenRevocation(service.KeyId, owner, _now), Ct)).ShouldBe(RevokeTokenStatus.Revoked);
    }

    [Fact]
    public async Task Touch_records_the_last_use()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var request = Personal(person, null);
        var other = Personal(person, null);
        await store.MintAsync(request, Ct);
        await store.MintAsync(other, Ct);

        await store.TouchAsync(request.KeyId, _now.AddMinutes(7), Ct);
        await store.TouchAsync("alvo_pat_unknown", _now, Ct);

        (await store.FindAsync(request.KeyId, Ct)).ShouldNotBeNull().LastUsedAt.ShouldBe(_now.AddMinutes(7));
        (await store.FindAsync(other.KeyId, Ct)).ShouldNotBeNull().LastUsedAt.ShouldBeNull();
        (await store.ListAsync(person, Ct)).Single(view => view.KeyId == request.KeyId).LastUsedAt.ShouldBe(_now.AddMinutes(7));
    }

    /// <summary>
    /// Five mints of one person at once against a cap of two store exactly two: the account-row lock serialises the count
    /// with the insert. On PostgreSQL the mints really overlap; on SQLite the write gate serialises them anyway.
    /// </summary>
    [Fact]
    public async Task Concurrent_mints_at_the_cap_never_exceed_it()
    {
        var (store, services) = await StoreAsync();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var person = await SignedUpAsync(services);

            var outcomes = await AllAtOnceAsync(
                Enumerable.Range(0, 5).Select(_ => (Func<Task<MintTokenOutcome>>)(() => store.MintAsync(Personal(person, null, maxLive: 2), Ct))));

            outcomes.Count(outcome => outcome.Status == MintTokenStatus.Minted).ShouldBe(2, $"iteration {iteration}");
            outcomes.Count(outcome => outcome.Status == MintTokenStatus.LimitReached).ShouldBe(3, $"iteration {iteration}");
            (await store.ListAsync(person, Ct)).Count.ShouldBe(2, $"iteration {iteration}");
        }
    }

    [Fact]
    public async Task Removing_a_member_revokes_their_personal_tokens_for_that_tenant_only()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var (other, _, _) = await TeamAsync(services);
        await TenantSeed.AddMemberAsync(services, other, member, ["viewer"], isOwner: false, _now);
        var here = Personal(member, tenant);
        var tenantless = Personal(member, null);
        var elsewhere = Personal(member, other);
        var owners = Personal(owner, tenant);
        await MintAllAsync(store, here, tenantless, elsewhere, owners);

        (await Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, owner, member, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, here.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, tenantless.KeyId)).ShouldBeNull("a tenantless token is not the tenant's");
        (await RevokedAtAsync(store, elsewhere.KeyId)).ShouldBeNull("another tenant's membership is untouched");
        (await RevokedAtAsync(store, owners.KeyId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_member_leaving_revokes_the_same()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var here = Personal(member, tenant);
        var tenantless = Personal(member, null);
        var owners = Personal(owner, tenant);
        await MintAllAsync(store, here, tenantless, owners);

        (await Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, member, member, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, here.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, tenantless.KeyId)).ShouldBeNull();
        (await RevokedAtAsync(store, owners.KeyId)).ShouldBeNull();
    }

    [Fact]
    public async Task Removing_an_owner_revokes_the_service_tokens_they_minted_and_no_one_else_s()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(services);
        var coOwner = await CoOwnerAsync(services, tenant);
        var (other, otherOwner, _) = await TeamAsync(services);
        await TenantSeed.AddMemberAsync(services, other, coOwner, ["dispatcher"], isOwner: true, _now);
        var theirs = Service(coOwner, tenant, ["dispatcher"]);
        var theirPersonal = Personal(coOwner, tenant);
        var theirsElsewhere = Service(coOwner, other, ["dispatcher"]);
        var mine = Service(owner, tenant, ["dispatcher"]);
        var otherOwners = Service(otherOwner, other, ["dispatcher"]);
        await MintAllAsync(store, theirs, theirPersonal, theirsElsewhere, mine, otherOwners);

        (await Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, owner, coOwner, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, theirs.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, theirPersonal.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, theirsElsewhere.KeyId)).ShouldBeNull("only the tenant they left");
        (await RevokedAtAsync(store, mine.KeyId)).ShouldBeNull("another owner's service token lives");
        (await RevokedAtAsync(store, otherOwners.KeyId)).ShouldBeNull();
    }

    [Fact]
    public async Task Demoting_an_owner_revokes_their_service_tokens_and_leaves_their_personal_tokens()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(services);
        var coOwner = await CoOwnerAsync(services, tenant);
        var theirs = Service(coOwner, tenant, ["dispatcher"]);
        var theirPersonal = Personal(coOwner, tenant);
        var mine = Service(owner, tenant, ["dispatcher"]);
        await MintAllAsync(store, theirs, theirPersonal, mine);

        (await Tenants(services).UpdateMemberAsync(Demotion(tenant, owner, coOwner, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, theirs.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, theirPersonal.KeyId)).ShouldBeNull("a personal token's roles are the member's live ones");
        (await RevokedAtAsync(store, mine.KeyId)).ShouldBeNull();
    }

    /// <summary>
    /// A demotion cannot reach <see cref="MemberChangeStatus.LastOwner"/> sequentially (an owner never changes their own
    /// membership, and another owner's demotion leaves the actor one), so the refusals pinned are a non-owner's demotion and
    /// the sole owner's leaving — the write the last-owner check really refuses.
    /// </summary>
    [Fact]
    public async Task A_refused_demotion_revokes_nothing()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var service = Service(owner, tenant, ["dispatcher"]);
        var personal = Personal(owner, tenant);
        await MintAllAsync(store, service, personal);

        (await Tenants(services).UpdateMemberAsync(Demotion(tenant, member, owner, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.NotOwner);
        (await Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, owner, owner, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.LastOwner);

        (await RevokedAtAsync(store, service.KeyId)).ShouldBeNull();
        (await RevokedAtAsync(store, personal.KeyId)).ShouldBeNull();
    }

    [Fact]
    public async Task Changing_a_member_s_roles_revokes_nothing()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var coOwner = await CoOwnerAsync(services, tenant);
        var service = Service(coOwner, tenant, ["dispatcher"]);
        var personal = Personal(member, tenant);
        await MintAllAsync(store, service, personal);

        (await Tenants(services).UpdateMemberAsync(
            new MemberUpdate(tenant, owner, coOwner, ["planner"], IsOwner: true, _now.AddHours(1)), Ct)).ShouldBe(MemberChangeStatus.Changed);
        (await Tenants(services).UpdateMemberAsync(
            new MemberUpdate(tenant, owner, member, ["planner"], IsOwner: null, _now.AddHours(1)), Ct)).ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, service.KeyId)).ShouldBeNull("a service token's roles are fixed at mint");
        (await RevokedAtAsync(store, personal.KeyId)).ShouldBeNull("a personal token's roles are the member's live ones");
    }

    [Fact]
    public async Task Disabling_an_account_revokes_every_token_it_minted_and_enabling_revives_none()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var tenantless = Personal(owner, null);
        var personal = Personal(owner, tenant);
        var service = Service(owner, tenant, ["dispatcher"]);
        var members = Personal(member, tenant);
        await MintAllAsync(store, tenantless, personal, service, members);
        var accounts = services.GetRequiredService<IAccountStore>();

        (await accounts.SetDisabledAsync(owner, disabled: true, _now.AddHours(1), Ct)).ShouldBeTrue();
        (await accounts.SetDisabledAsync(owner, disabled: false, _now.AddHours(2), Ct)).ShouldBeTrue();

        (await RevokedAtAsync(store, tenantless.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, personal.KeyId)).ShouldBe(_now.AddHours(1));
        (await RevokedAtAsync(store, service.KeyId)).ShouldBe(_now.AddHours(1), "a service token dies with its minter's account");
        (await RevokedAtAsync(store, members.KeyId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_rejoined_member_s_old_personal_token_stays_revoked()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(services);
        var old = Personal(member, tenant);
        await MintAllAsync(store, old);

        (await Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, owner, member, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed);
        await TenantSeed.AddMemberAsync(services, tenant, member, ["viewer"], isOwner: false, _now.AddHours(2));

        (await RevokedAtAsync(store, old.KeyId)).ShouldBe(_now.AddHours(1));
    }

    /// <summary>
    /// Review Focus 2: two owners, one minting a service token while the other demotes them. The mint takes the tenant's lock
    /// before it reads the minter's membership, so it either judges the demotion (<see cref="MintTokenStatus.NotOwner"/>, no
    /// row) or commits first and is swept by it — never a live service token of a non-owner.
    /// </summary>
    [Fact]
    public async Task A_service_mint_racing_its_minter_s_demotion_never_outlives_it()
    {
        var (store, services) = await StoreAsync();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, owner, _) = await TeamAsync(services);
            var coOwner = await CoOwnerAsync(services, tenant);
            var request = Service(coOwner, tenant, ["dispatcher"]);

            var (minted, demoted) = await RaceAsync(
                () => store.MintAsync(request, Ct),
                () => Tenants(services).UpdateMemberAsync(Demotion(tenant, owner, coOwner, _now.AddHours(1)), Ct));

            demoted.ShouldBe(MemberChangeStatus.Changed, $"iteration {iteration}");
            await ShouldNotOutliveAsync(store, request, minted, MintTokenStatus.NotOwner, iteration);
        }
    }

    /// <summary>Review Focus 2: a member minting a personal token for the tenant while an owner removes them.</summary>
    [Fact]
    public async Task A_personal_mint_racing_the_person_s_removal_never_outlives_it()
    {
        var (store, services) = await StoreAsync();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, owner, member) = await TeamAsync(services);
            var request = Personal(member, tenant);

            var (minted, removed) = await RaceAsync(
                () => store.MintAsync(request, Ct),
                () => Tenants(services).RemoveMemberAsync(new MemberRemoval(tenant, owner, member, _now.AddHours(1)), Ct));

            removed.ShouldBe(MemberChangeStatus.Changed, $"iteration {iteration}");
            await ShouldNotOutliveAsync(store, request, minted, MintTokenStatus.NotFound, iteration);
        }
    }

    /// <summary>Review Focus 2: a person minting while an operator disables their account — the account-row lock decides.</summary>
    [Fact]
    public async Task A_mint_racing_a_ban_never_outlives_it()
    {
        var (store, services) = await StoreAsync();
        var accounts = services.GetRequiredService<IAccountStore>();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, owner, _) = await TeamAsync(services);
            var request = Service(owner, tenant, ["dispatcher"]);

            var (minted, disabled) = await RaceAsync(
                () => store.MintAsync(request, Ct),
                () => accounts.SetDisabledAsync(owner, disabled: true, _now.AddHours(1), Ct));

            disabled.ShouldBeTrue($"iteration {iteration}");
            await ShouldNotOutliveAsync(store, request, minted, MintTokenStatus.NoAccount, iteration);
        }
    }

    /// <summary>Runs both operations at once, released together.</summary>
    private static async Task<(TFirst First, TSecond Second)> RaceAsync<TFirst, TSecond>(
        Func<Task<TFirst>> first, Func<Task<TSecond>> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = Task.Run(async () => { await start.Task; return await first(); }, Ct);
        var two = Task.Run(async () => { await start.Task; return await second(); }, Ct);
        start.SetResult();
        return (await one, await two);
    }

    /// <summary>The mint either was <paramref name="refusal"/> and stored nothing, or stored a row that is already revoked.</summary>
    private static async Task ShouldNotOutliveAsync(
        IAppTokenStore store, MintTokenRequest request, MintTokenOutcome minted, MintTokenStatus refusal, int iteration)
    {
        var record = await store.FindAsync(request.KeyId, Ct);
        if (minted.Status == refusal)
        {
            record.ShouldBeNull($"iteration {iteration}: a refused mint stores nothing");
            return;
        }

        minted.Status.ShouldBe(MintTokenStatus.Minted, $"iteration {iteration}");
        record.ShouldNotBeNull($"iteration {iteration}").RevokedAt.ShouldNotBeNull($"iteration {iteration}: the mint outlived its reason");
    }

    private static async Task MintAllAsync(IAppTokenStore store, params MintTokenRequest[] requests)
    {
        foreach (var request in requests)
        {
            (await store.MintAsync(request, Ct)).Status.ShouldBe(MintTokenStatus.Minted);
        }
    }

    /// <summary>A fresh account, added to <paramref name="tenant"/> as an owner holding <c>dispatcher</c>.</summary>
    private static async Task<UserId> CoOwnerAsync(IServiceProvider services, TenantId tenant)
    {
        var coOwner = await SignedUpAsync(services, "Grace");
        await TenantSeed.AddMemberAsync(services, tenant, coOwner, ["dispatcher"], isOwner: true, _now);
        return coOwner;
    }

    private static MemberUpdate Demotion(TenantId tenant, UserId actor, UserId member, DateTimeOffset now) =>
        new(tenant, actor, member, Roles: null, IsOwner: false, now);

    private static ITenantStore Tenants(IServiceProvider services) => services.GetRequiredService<ITenantStore>();

    [Fact]
    public void The_api_keys_table_is_a_reserved_framework_name() =>
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_api_keys");

    /// <summary>Runs every operation at once, released together, and returns their results.</summary>
    private static async Task<TResult[]> AllAtOnceAsync<TResult>(IEnumerable<Func<Task<TResult>>> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = operations.Select(operation => Task.Run(async () => { await start.Task; return await operation(); }, Ct)).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    /// <summary>A personal token of <paramref name="minter"/>'s, scoped to <c>orders:read</c>, expiring in thirty days.</summary>
    private static MintTokenRequest Personal(UserId minter, TenantId? tenant, int maxLive = 100, DateTimeOffset? now = null) =>
        new(
            "alvo_pat_" + Guid.NewGuid().ToString("N"), ApiKeyKind.Personal, Hash(), "ci", minter, minter, tenant, [], ["orders:read"],
            _now.AddDays(30), maxLive, now ?? _now);

    /// <summary>A service token of <paramref name="minter"/>'s into <paramref name="tenant"/>, acting as a fresh user.</summary>
    private static MintTokenRequest Service(UserId minter, TenantId tenant, IReadOnlyList<string> roles, DateTimeOffset? now = null) =>
        new(
            "alvo_svc_" + Guid.NewGuid().ToString("N"), ApiKeyKind.Service, Hash(), "ci", minter, UserId.New(), tenant, roles,
            ["orders:read"], _now.AddDays(30), 100, now ?? _now);

    private static string Hash() => Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    private static async Task<string[]> KeysAsync(IAppTokenStore store, UserId person) =>
        [.. (await store.ListAsync(person, Ct)).Select(view => view.KeyId)];

    private static async Task<DateTimeOffset?> RevokedAtAsync(IAppTokenStore store, string keyId) =>
        (await store.FindAsync(keyId, Ct)).ShouldNotBeNull().RevokedAt;

    /// <summary>Asserts two views are equal, roles and scopes compared by value.</summary>
    private static void Same(AppTokenView actual, AppTokenView expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles, Scopes = actual.Scopes });
        actual.Roles.ShouldBe(expected.Roles);
        actual.Scopes.ShouldBe(expected.Scopes);
    }

    /// <summary>Starts a fresh database and returns its app-token store and container.</summary>
    private async Task<(IAppTokenStore Store, IServiceProvider Services)> StoreAsync()
    {
        var services = await StartAsync();
        return (services.GetRequiredService<IAppTokenStore>(), services);
    }

    /// <summary>Signs a fresh identity up and returns its account.</summary>
    private static async Task<UserId> SignedUpAsync(IServiceProvider services, string? name = "Ada")
    {
        var identity = new VerifiedIdentity("google", "sub-" + Guid.NewGuid().ToString("N"), "ada@gmail.com", true, name, null);
        var request = new SignInRequest(
            identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now.AddDays(180), null);
        var outcome = await services.GetRequiredService<IAccountStore>().SignInAsync(request, Ct);
        return outcome.Account!.Value;
    }

    /// <summary>
    /// A shared tenant <c>Acme</c> whose owner <c>Ada</c> holds <c>dispatcher</c> and <c>planner</c>, and a member who holds
    /// <c>viewer</c>.
    /// </summary>
    private static async Task<(TenantId Tenant, UserId Owner, UserId Member)> TeamAsync(IServiceProvider services)
    {
        var owner = await SignedUpAsync(services);
        var member = await SignedUpAsync(services, "Bob");
        var tenant = TenantId.New();
        (await services.GetRequiredService<ITenantStore>().CreateSharedAsync(
            new CreateTenantRequest(tenant, "Acme", owner, ["dispatcher", "planner"], 5, _now), Ct)).Status.ShouldBe(CreateTenantStatus.Created);
        await TenantSeed.AddMemberAsync(services, tenant, member, ["viewer"], isOwner: false, _now.AddMinutes(1));
        return (tenant, owner, member);
    }

    private static string ApiKeys(IServiceProvider services) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + AlvoFrameworkTables.ApiKeysSuffix;

    private static async Task<long> CountAsync(IServiceProvider services) => Convert.ToInt64(
        await ScalarAsync(services, $"SELECT COUNT(*) FROM {ApiKeys(services)}"), CultureInfo.InvariantCulture);

    private static async Task<long> VersionAsync(IServiceProvider services, TenantId tenant) => Convert.ToInt64(
        await ScalarAsync(
            services,
            $"SELECT version FROM {services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix}{AlvoFrameworkTables.TenantsSuffix} WHERE id = '{tenant.Value:D}'"),
        CultureInfo.InvariantCulture);

    /// <summary>Every stored column of one token, concatenated — to prove what is and is not stored.</summary>
    private static async Task<string> RowTextAsync(IServiceProvider services, string keyId) => (string)(await ScalarAsync(
        services,
        "SELECT key_id || kind || secret_hash || name || user_id || minted_by || COALESCE(tenant_id, '') || roles || scopes "
        + $"|| created_at || expires_at FROM {ApiKeys(services)} WHERE key_id = '{keyId}'"))!;

    /// <summary>Runs <paramref name="sql"/> on its own connection and returns the first column of the first row.</summary>
    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
