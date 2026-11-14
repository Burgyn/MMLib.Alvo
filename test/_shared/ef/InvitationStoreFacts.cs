using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The tenant registry's invitations — creating, listing and revoking them, and an owner's invitations dying with their
/// ownership — the same facts on SQLite and on PostgreSQL (accounts spec §6.1, §6.4; S4 plan D2, D6, D12). Accounts are
/// created through <see cref="IAccountStore.SignInAsync"/>, tenants through <see cref="ITenantStore.CreateSharedAsync"/>,
/// and members no port member writes yet through <see cref="TenantSeed"/>.
/// </summary>
public abstract class InvitationStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

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
    public async Task An_owner_creates_an_invitation_with_its_hash_roles_and_bounds()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var request = Invite(tenant, owner, ["viewer", "planner"], grantsOwner: true, email: "bob@example.com", maxUses: 3);

        var outcome = await store.CreateInvitationAsync(request, Ct);

        var expected = new InvitationView(
            request.Id, tenant, ["viewer", "planner"], GrantsOwner: true, "bob@example.com", MaxUses: 3, Uses: 0,
            _now.AddDays(7), owner, _now, RevokedAt: null);
        outcome.Status.ShouldBe(MemberChangeStatus.Changed);
        Same(outcome.Invitation.ShouldNotBeNull(), expected);
        Same((await store.ListInvitationsAsync(tenant, Ct)).ShouldHaveSingleItem(), expected);
        (await ColumnAsync(services, request.Id, "code_hash")).ShouldBe(request.CodeHash, "only the code's hash is stored");
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_cannot_invite()
    {
        var (store, services) = await StoreAsync();
        var (tenant, _, member) = await TeamAsync(store, services);

        var outcome = await store.CreateInvitationAsync(Invite(tenant, member), Ct);

        outcome.ShouldBe(new CreateInvitationOutcome(MemberChangeStatus.NotOwner, null));
        (await CountAsync(services)).ShouldBe(0);
        (await VersionAsync(services, tenant)).ShouldBe(0, "a refusal rolls its version bump back");
    }

    [Fact]
    public async Task A_non_member_and_an_unknown_tenant_get_not_found()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var stranger = await SignedUpAsync(services);

        (await store.CreateInvitationAsync(Invite(tenant, stranger), Ct)).Status.ShouldBe(MemberChangeStatus.NotFound);
        (await store.CreateInvitationAsync(Invite(TenantId.New(), owner), Ct)).Status.ShouldBe(MemberChangeStatus.NotFound);
        (await store.RevokeInvitationAsync(Revoke(tenant, stranger, Guid.NewGuid()), Ct)).ShouldBe(MemberChangeStatus.NotFound);
        (await store.RevokeInvitationAsync(Revoke(TenantId.New(), owner, Guid.NewGuid()), Ct)).ShouldBe(MemberChangeStatus.NotFound);

        (await CountAsync(services)).ShouldBe(0);
    }

    [Fact]
    public async Task A_personal_tenant_takes_no_invitation()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services);
        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);

        var outcome = await store.CreateInvitationAsync(Invite(personal, owner), Ct);

        outcome.ShouldBe(new CreateInvitationOutcome(MemberChangeStatus.PersonalTenant, null));
        (await CountAsync(services)).ShouldBe(0);
    }

    [Fact]
    public async Task Invitations_list_newest_first_with_every_state()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var (other, _, _) = await TeamAsync(store, services);
        var active = Invite(tenant, owner, now: _now.AddMinutes(1));
        var expired = Invite(tenant, owner, now: _now.AddMinutes(2)) with { ExpiresAt = _now.AddMinutes(3) };
        var revoked = Invite(tenant, owner, now: _now.AddMinutes(4));
        var usedUp = Invite(tenant, owner, maxUses: 2, now: _now.AddMinutes(4).AddSeconds(30));
        var tiedLow = Invite(tenant, owner, now: _now.AddMinutes(5)) with { Id = Ordered(0x10) };
        var tiedHigh = Invite(tenant, owner, now: _now.AddMinutes(5)) with { Id = Ordered(0xF0) };
        foreach (var request in new[] { tiedLow, active, revoked, usedUp, expired, tiedHigh })
        {
            (await store.CreateInvitationAsync(request, Ct)).Status.ShouldBe(MemberChangeStatus.Changed);
        }

        (await store.RevokeInvitationAsync(Revoke(tenant, owner, revoked.Id, _now.AddMinutes(6)), Ct)).ShouldBe(MemberChangeStatus.Changed);
        await ScalarAsync(services, $"UPDATE {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)} SET uses = 2 WHERE id = '{usedUp.Id:D}'");
        await TenantSeed.AddInvitationAsync(services, Invite(other, owner));

        var listed = await store.ListInvitationsAsync(tenant, Ct);

        listed.Select(view => view.Id).ShouldBe([tiedHigh.Id, tiedLow.Id, usedUp.Id, revoked.Id, expired.Id, active.Id]);
        (listed[2].Uses, listed[2].MaxUses).ShouldBe((2, 2));
        listed[3].RevokedAt.ShouldBe(_now.AddMinutes(6));
        listed[4].ExpiresAt.ShouldBe(_now.AddMinutes(3), "the store lists an expired invitation as stored; its state is the reader's");
        listed.Where(view => view.Id != revoked.Id).ShouldAllBe(view => view.RevokedAt == null);
    }

    [Fact]
    public async Task An_owner_revokes_any_invitation_of_the_tenant_once_and_again()
    {
        var (store, services) = await StoreAsync();
        var (tenant, first, second) = await TwoOwnersAsync(store, services);
        var member = await SignedUpAsync(services);
        await TenantSeed.AddMemberAsync(services, tenant, member, [], isOwner: false, _now);
        var request = Invite(tenant, first);
        await store.CreateInvitationAsync(request, Ct);

        (await store.RevokeInvitationAsync(Revoke(tenant, member, request.Id), Ct)).ShouldBe(MemberChangeStatus.NotOwner);
        (await RevokedAtAsync(store, tenant, request.Id)).ShouldBeNull("a refusal revokes nothing");
        (await store.RevokeInvitationAsync(Revoke(tenant, second, request.Id, _now.AddHours(1)), Ct))
            .ShouldBe(MemberChangeStatus.Changed, "any owner revokes any invitation of the tenant");
        (await store.RevokeInvitationAsync(Revoke(tenant, first, request.Id, _now.AddHours(2)), Ct))
            .ShouldBe(MemberChangeStatus.Changed, "revoking is idempotent");

        (await RevokedAtAsync(store, tenant, request.Id)).ShouldBe(_now.AddHours(1), "the first revocation's instant is kept");
    }

    [Fact]
    public async Task Revoking_another_tenant_s_invitation_is_no_such_invitation()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var (other, otherOwner, _) = await TeamAsync(store, services);
        var foreign = Invite(other, otherOwner);
        await store.CreateInvitationAsync(foreign, Ct);

        (await store.RevokeInvitationAsync(Revoke(tenant, owner, foreign.Id), Ct)).ShouldBe(MemberChangeStatus.NoSuchInvitation);
        (await store.RevokeInvitationAsync(Revoke(tenant, owner, Guid.NewGuid()), Ct)).ShouldBe(MemberChangeStatus.NoSuchInvitation);

        (await RevokedAtAsync(store, other, foreign.Id)).ShouldBeNull();
        (await VersionAsync(services, tenant)).ShouldBe(0, "a refusal rolls its version bump back");
    }

    [Fact]
    public async Task Demoting_an_owner_revokes_the_invitations_they_created_and_no_one_else_s()
    {
        var (store, services) = await StoreAsync();
        var (tenant, first, second) = await TwoOwnersAsync(store, services);
        var (live, earlier, fellows) = (Invite(tenant, first), Invite(tenant, first), Invite(tenant, second));
        var elsewhere = TenantId.New();
        await store.CreateSharedAsync(new CreateTenantRequest(elsewhere, "Other", first, [], 5, _now), Ct);
        var elsewhereInvitation = Invite(elsewhere, first);
        foreach (var request in new[] { live, earlier, fellows, elsewhereInvitation })
        {
            await store.CreateInvitationAsync(request, Ct);
        }

        await store.RevokeInvitationAsync(Revoke(tenant, first, earlier.Id, _now.AddMinutes(1)), Ct);
        (await store.UpdateMemberAsync(Update(tenant, second, first, ["viewer"], isOwner: null), Ct)).ShouldBe(MemberChangeStatus.Changed);
        (await RevokedAtAsync(store, tenant, live.Id)).ShouldBeNull("a change of roles alone keeps the owner an owner");

        (await store.UpdateMemberAsync(Update(tenant, second, first, roles: null, isOwner: false), Ct)).ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, tenant, live.Id)).ShouldBe(_now.AddHours(1), "the demotion's instant");
        (await RevokedAtAsync(store, tenant, earlier.Id)).ShouldBe(_now.AddMinutes(1), "an earlier revocation keeps its instant");
        (await RevokedAtAsync(store, tenant, fellows.Id)).ShouldBeNull("another owner's invitation lives on");
        (await RevokedAtAsync(store, elsewhere, elsewhereInvitation.Id)).ShouldBeNull("ownership elsewhere is untouched");
    }

    [Fact]
    public async Task Removing_an_owner_or_an_owner_leaving_revokes_their_invitations()
    {
        var (store, services) = await StoreAsync();
        var (tenant, first, second) = await TwoOwnersAsync(store, services);
        var third = await SignedUpAsync(services);
        await TenantSeed.AddMemberAsync(services, tenant, third, [], isOwner: true, _now);
        var (removed, leaving, staying) = (Invite(tenant, first), Invite(tenant, third), Invite(tenant, second));
        foreach (var request in new[] { removed, leaving, staying })
        {
            await store.CreateInvitationAsync(request, Ct);
        }

        (await store.RemoveMemberAsync(new MemberRemoval(tenant, second, first, _now.AddHours(1)), Ct)).ShouldBe(MemberChangeStatus.Changed);
        (await store.RemoveMemberAsync(new MemberRemoval(tenant, third, third, _now.AddHours(2)), Ct)).ShouldBe(MemberChangeStatus.Changed);

        (await RevokedAtAsync(store, tenant, removed.Id)).ShouldBe(_now.AddHours(1), "a removed owner's invitations die");
        (await RevokedAtAsync(store, tenant, leaving.Id)).ShouldBe(_now.AddHours(2), "a leaving owner's invitations die");
        (await RevokedAtAsync(store, tenant, staying.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_demotion_revokes_nothing()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var request = Invite(tenant, owner);
        await store.CreateInvitationAsync(request, Ct);

        (await store.UpdateMemberAsync(Update(tenant, owner, owner, roles: null, isOwner: false), Ct)).ShouldBe(MemberChangeStatus.Self);
        (await store.UpdateMemberAsync(Update(tenant, member, owner, roles: null, isOwner: false), Ct)).ShouldBe(MemberChangeStatus.NotOwner);
        (await store.RemoveMemberAsync(new MemberRemoval(tenant, member, owner, _now.AddHours(1)), Ct)).ShouldBe(MemberChangeStatus.NotOwner);
        (await store.RemoveMemberAsync(new MemberRemoval(tenant, owner, owner, _now.AddHours(1)), Ct)).ShouldBe(MemberChangeStatus.LastOwner);

        (await RevokedAtAsync(store, tenant, request.Id)).ShouldBeNull("every refusal rolled back whatever it wrote");
    }

    /// <summary>
    /// Review Focus 2 (S4 plan D6): an owner creates an invitation while a fellow owner demotes them. Both go through the
    /// tenant's version bump, so either the creation judges a non-owner and writes nothing, or the demotion's sweep sees
    /// the new invitation and revokes it — never an invitation of a non-owner left live.
    /// </summary>
    [Fact]
    public async Task An_invitation_created_while_its_creator_is_demoted_never_outlives_the_demotion()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, first, second) = await TwoOwnersAsync(store, services);
            var request = Invite(tenant, first);

            var (created, demoted) = await RaceAsync(
                async () => { await Task.Delay(iteration % 4, Ct); return await store.CreateInvitationAsync(request, Ct); },
                () => store.UpdateMemberAsync(Update(tenant, second, first, roles: null, isOwner: false), Ct));

            demoted.ShouldBe(MemberChangeStatus.Changed, $"iteration {iteration}");
            created.Status.ShouldBeOneOf([MemberChangeStatus.Changed, MemberChangeStatus.NotOwner], $"iteration {iteration}");
            var listed = await store.ListInvitationsAsync(tenant, Ct);
            if (created.Status == MemberChangeStatus.NotOwner)
            {
                listed.ShouldBeEmpty($"iteration {iteration}");
            }
            else
            {
                listed.ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull($"iteration {iteration}: the invitation outlived the demotion");
            }
        }
    }

    [Fact]
    public void The_invitations_table_is_a_reserved_framework_name() =>
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_tenant_invitations");

    /// <summary>Starts a fresh database and returns its tenant store and container.</summary>
    private protected async Task<(ITenantStore Store, IServiceProvider Services)> StoreAsync()
    {
        var services = await StartAsync();
        return (services.GetRequiredService<ITenantStore>(), services);
    }

    /// <summary>Signs a fresh identity up and returns its account.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="email">The identity's email.</param>
    /// <param name="trusted">Whether the identity's email is trusted.</param>
    /// <param name="name">The identity's display name.</param>
    private protected static async Task<UserId> SignedUpAsync(
        IServiceProvider services, string? email = "ada@gmail.com", bool trusted = true, string? name = "Ada")
    {
        var identity = new VerifiedIdentity("google", "sub-" + Guid.NewGuid().ToString("N"), email, trusted, name, null);
        var request = new SignInRequest(
            identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now.AddDays(180), null);
        var outcome = await services.GetRequiredService<IAccountStore>().SignInAsync(request, Ct);
        return outcome.Account!.Value;
    }

    /// <summary>A shared tenant <c>Acme</c> whose owner holds <c>dispatcher</c>, and a member who holds <c>viewer</c>.</summary>
    private protected static async Task<(TenantId Tenant, UserId Owner, UserId Member)> TeamAsync(ITenantStore store, IServiceProvider services)
    {
        var owner = await SignedUpAsync(services);
        var member = await SignedUpAsync(services);
        var tenant = TenantId.New();
        (await store.CreateSharedAsync(new CreateTenantRequest(tenant, "Acme", owner, ["dispatcher"], 5, _now), Ct)).Status
            .ShouldBe(CreateTenantStatus.Created);
        await TenantSeed.AddMemberAsync(services, tenant, member, ["viewer"], isOwner: false, _now.AddMinutes(1));
        return (tenant, owner, member);
    }

    /// <summary>A shared tenant <c>Acme</c> with two owners and no other member.</summary>
    private protected static async Task<(TenantId Tenant, UserId First, UserId Second)> TwoOwnersAsync(ITenantStore store, IServiceProvider services)
    {
        var first = await SignedUpAsync(services);
        var second = await SignedUpAsync(services);
        var tenant = TenantId.New();
        (await store.CreateSharedAsync(new CreateTenantRequest(tenant, "Acme", first, [], 5, _now), Ct)).Status
            .ShouldBe(CreateTenantStatus.Created);
        await TenantSeed.AddMemberAsync(services, tenant, second, [], isOwner: true, _now.AddMinutes(1));
        return (tenant, first, second);
    }

    /// <summary>An invitation of <paramref name="actor"/>'s into <paramref name="tenant"/>, under a fresh code hash.</summary>
    private protected static CreateInvitationRequest Invite(
        TenantId tenant, UserId actor, IReadOnlyList<string>? roles = null, bool grantsOwner = false, string? email = null,
        int maxUses = 1, DateTimeOffset? now = null) =>
        new(Guid.NewGuid(), tenant, actor, Hash(), roles ?? ["viewer"], grantsOwner, email, maxUses, _now.AddDays(7), now ?? _now);

    /// <summary>A fresh code hash, in the shape the core stores: lowercase hex SHA-256.</summary>
    private protected static string Hash() => Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(16)));

    /// <summary>A revocation of <paramref name="invitation"/> by <paramref name="actor"/>.</summary>
    private protected static InvitationRevocation Revoke(TenantId tenant, UserId actor, Guid invitation, DateTimeOffset? now = null) =>
        new(tenant, actor, invitation, now ?? _now.AddHours(1));

    /// <summary>A change of <paramref name="member"/> by <paramref name="actor"/>, an hour after the start.</summary>
    private protected static MemberUpdate Update(TenantId tenant, UserId actor, UserId member, IReadOnlyList<string>? roles, bool? isOwner) =>
        new(tenant, actor, member, roles, isOwner, _now.AddHours(1));

    /// <summary>An id whose text sorts by <paramref name="order"/> — for the facts that pin a tie-break by id.</summary>
    private protected static Guid Ordered(byte order) =>
        Guid.Parse(order.ToString("x2", CultureInfo.InvariantCulture) + "000000-0000-4000-8000-000000000000", CultureInfo.InvariantCulture);

    /// <summary>Runs two operations at once, released together, and returns both results.</summary>
    private protected static async Task<(TFirst First, TSecond Second)> RaceAsync<TFirst, TSecond>(
        Func<Task<TFirst>> first, Func<Task<TSecond>> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = Task.Run(async () => { await start.Task; return await first(); }, Ct);
        var two = Task.Run(async () => { await start.Task; return await second(); }, Ct);
        start.SetResult();
        await Task.WhenAll(one, two);
        return (await one, await two);
    }

    /// <summary>The <c>revoked_at</c> of one invitation, as the owners' list shows it.</summary>
    private protected static async Task<DateTimeOffset?> RevokedAtAsync(ITenantStore store, TenantId tenant, Guid invitation) =>
        (await store.ListInvitationsAsync(tenant, Ct)).Single(view => view.Id == invitation).RevokedAt;

    /// <summary>Asserts two invitation views are equal, roles compared by value.</summary>
    private protected static void Same(InvitationView actual, InvitationView expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

    /// <summary>The prefixed name of the framework table <paramref name="suffix"/> names.</summary>
    protected static string TableOf(IServiceProvider services, string suffix) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix;

    /// <summary>How many invitations are stored.</summary>
    protected static async Task<long> CountAsync(IServiceProvider services) => Convert.ToInt64(
        await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)}"),
        CultureInfo.InvariantCulture);

    /// <summary>One stored column of one invitation.</summary>
    protected static Task<object?> ColumnAsync(IServiceProvider services, Guid invitation, string column) => ScalarAsync(
        services,
        $"SELECT {column} FROM {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)} WHERE id = '{invitation:D}'");

    /// <summary>The stored <c>version</c> of <paramref name="tenant"/>.</summary>
    protected static async Task<long> VersionAsync(IServiceProvider services, TenantId tenant) => Convert.ToInt64(
        await ScalarAsync(services, $"SELECT version FROM {TableOf(services, AlvoFrameworkTables.TenantsSuffix)} WHERE id = '{tenant.Value:D}'"),
        CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="sql"/> on its own connection and returns the first column of the first row.</summary>
    protected static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
