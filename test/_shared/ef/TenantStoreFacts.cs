using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The tenant registry — tenants, memberships, the creator's cap — the same facts on SQLite and on PostgreSQL (accounts
/// spec §6.1, §6.4; S3 plan D2, D3, D9, D15). Accounts are created through <see cref="IAccountStore.SignInAsync"/>; members
/// and personal tenants no port member writes yet are seeded with <see cref="TenantSeed"/>.
/// </summary>
public abstract class TenantStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] _tenantTables = ["alvo_tenants", "alvo_tenant_members"];

    /// <summary>Gets the instant every fact starts at.</summary>
    protected static DateTimeOffset Start => _now;

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
    public async Task Creating_a_tenant_makes_the_creator_its_owner_with_the_creator_roles()
    {
        var (store, services) = await StoreAsync();
        var creator = await SignedUpAsync(services);
        var tenant = TenantId.New();

        var outcome = await store.CreateSharedAsync(Create(tenant, creator, ["dispatcher", "planner"]), Ct);

        var expected = new StoredMembership(tenant, TenantKind.Shared, "Acme", ["dispatcher", "planner"], IsOwner: true, _now);
        outcome.Status.ShouldBe(CreateTenantStatus.Created);
        Same(outcome.Membership.ShouldNotBeNull(), expected);
        Same((await store.FindMembershipAsync(creator, tenant, Ct)).ShouldNotBeNull(), expected);
        (await store.ListMembershipsAsync(creator, Ct)).Count.ShouldBe(1);
        (await VersionAsync(services, tenant)).ShouldBe(0, "a new tenant starts at version 0");
    }

    [Fact]
    public async Task Memberships_list_the_personal_tenant_first_then_by_joining()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var (early, laterHigh, laterLow, joined, personal) = (Tenant(1), Tenant(0xF0), Tenant(0xE0), Tenant(5), Tenant(9));
        await store.CreateSharedAsync(Create(early, person, [], _now.AddMinutes(1)), Ct);
        await store.CreateSharedAsync(Create(laterHigh, person, [], _now.AddMinutes(3)), Ct);
        await store.CreateSharedAsync(Create(laterLow, person, [], _now.AddMinutes(3)), Ct);
        await store.CreateSharedAsync(Create(joined, other, [], _now), Ct);
        await TenantSeed.AddMemberAsync(services, joined, person, ["viewer"], isOwner: false, _now.AddMinutes(2));
        await TenantSeed.AddPersonalTenantAsync(services, personal, person, "Ada", _now.AddMinutes(4));

        var memberships = await store.ListMembershipsAsync(person, Ct);

        memberships.Select(membership => membership.Tenant).ShouldBe([personal, early, joined, laterLow, laterHigh]);
        memberships[0].Kind.ShouldBe(TenantKind.Personal);
        memberships[0].Roles.ShouldBeEmpty();
        memberships[2].IsOwner.ShouldBeFalse("a membership in someone else's tenant is not an ownership");
        memberships[2].Roles.ShouldBe(["viewer"]);
    }

    [Fact]
    public async Task Find_answers_a_membership_only_to_its_member()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services);
        var stranger = await SignedUpAsync(services);
        var tenant = TenantId.New();
        await store.CreateSharedAsync(Create(tenant, owner, []), Ct);

        (await store.FindMembershipAsync(stranger, tenant, Ct)).ShouldBeNull("another person is no member");
        (await store.FindMembershipAsync(owner, TenantId.New(), Ct)).ShouldBeNull("an unknown tenant has no members");
        (await store.FindMembershipAsync(owner, null, Ct)).ShouldBeNull("no personal tenant yet");

        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);
        await TenantSeed.AddPersonalTenantAsync(services, TenantId.New(), stranger, "Bob", _now);

        var found = (await store.FindMembershipAsync(owner, null, Ct)).ShouldNotBeNull();
        found.Tenant.ShouldBe(personal, "the personal tenant is the owner's own, never another's");
        found.Kind.ShouldBe(TenantKind.Personal);
        found.Name.ShouldBe("Ada");
        (await store.FindMembershipAsync(stranger, personal, Ct)).ShouldBeNull("a personal tenant is its owner's alone");
    }

    [Fact]
    public async Task The_cap_counts_the_shared_tenants_the_account_created()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var foreign = TenantId.New();
        await store.CreateSharedAsync(Create(foreign, other, [], cap: 2), Ct);
        await TenantSeed.AddMemberAsync(services, foreign, person, [], isOwner: true, _now);
        await TenantSeed.AddPersonalTenantAsync(services, TenantId.New(), person, "Ada", _now);

        var first = await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct);
        var second = await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct);
        var refused = TenantId.New();
        var third = await store.CreateSharedAsync(Create(refused, person, [], cap: 2), Ct);

        first.Status.ShouldBe(CreateTenantStatus.Created, "a membership or a personal tenant is not a creation");
        second.Status.ShouldBe(CreateTenantStatus.Created);
        third.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.LimitReached, null));
        (await store.FindMembershipAsync(person, refused, Ct)).ShouldBeNull("a refusal writes nothing");
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(4);
        (await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 3), Ct)).Status
            .ShouldBe(CreateTenantStatus.Created, "the cap is the request's, so a raised one admits another");
    }

    [Fact]
    public async Task Concurrent_creates_at_the_cap_never_exceed_it()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var person = await SignedUpAsync(services);

            var outcomes = await Task.WhenAll(
                Enumerable.Range(0, 5).Select(_ => store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct)));

            outcomes.Count(outcome => outcome.Status == CreateTenantStatus.Created).ShouldBe(2, $"iteration {iteration}");
            outcomes.Count(outcome => outcome.Status == CreateTenantStatus.LimitReached).ShouldBe(3, $"iteration {iteration}");
            (await store.ListMembershipsAsync(person, Ct)).Count.ShouldBe(2, $"iteration {iteration}");
        }

        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(2 * Iterations);
    }

    [Fact]
    public async Task A_disabled_or_unknown_creator_creates_nothing()
    {
        var (store, services) = await StoreAsync();
        var disabled = await SignedUpAsync(services);
        (await services.GetRequiredService<IAccountStore>().SetDisabledAsync(disabled, disabled: true, _now, Ct)).ShouldBeTrue();

        var unknown = await store.CreateSharedAsync(Create(TenantId.New(), UserId.New(), []), Ct);
        var banned = await store.CreateSharedAsync(Create(TenantId.New(), disabled, []), Ct);

        unknown.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.NoAccount, null));
        banned.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.NoAccount, null));
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(0);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(0);
    }

    [Fact]
    public async Task Tenants_list_every_tenant_once_with_its_kind()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var (first, tiedHigh, tiedLow, personal) = (Tenant(1), Tenant(0xF0), Tenant(0xE0), Tenant(7));
        await store.CreateSharedAsync(Create(tiedHigh, person, [], _now.AddMinutes(2)), Ct);
        await store.CreateSharedAsync(Create(first, other, [], _now.AddMinutes(1)), Ct);
        await store.CreateSharedAsync(Create(tiedLow, other, [], _now.AddMinutes(2)), Ct);
        await TenantSeed.AddMemberAsync(services, first, person, [], isOwner: false, _now.AddMinutes(5));
        await TenantSeed.AddPersonalTenantAsync(services, personal, person, "Ada", _now.AddMinutes(3));

        (await store.ListTenantsAsync(Ct)).ShouldBe(
            [new(first, TenantKind.Shared), new(tiedLow, TenantKind.Shared), new(tiedHigh, TenantKind.Shared), new(personal, TenantKind.Personal)]);
    }

    [Fact]
    public void The_tables_are_reserved_framework_names() =>
        _tenantTables.Except(AlvoFrameworkTables.NamesFor("alvo"), StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public async Task An_owner_renames_and_a_member_cannot()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);

        (await store.RenameAsync(tenant, owner, "Acme Ltd", Ct)).ShouldBe(MemberChangeStatus.Changed);
        (await store.RenameAsync(tenant, member, "Hijacked", Ct)).ShouldBe(MemberChangeStatus.NotOwner);
        (await store.RenameAsync(personal, owner, "Mine", Ct)).ShouldBe(MemberChangeStatus.Changed, "a personal tenant's owner renames it");

        (await store.FindMembershipAsync(member, tenant, Ct))!.Name.ShouldBe("Acme Ltd");
        (await store.FindMembershipAsync(owner, null, Ct))!.Name.ShouldBe("Mine");
        (await VersionAsync(services, tenant)).ShouldBe(1, "a refused write rolls its version bump back");
    }

    [Fact]
    public async Task A_non_member_gets_not_found_for_every_write_and_null_for_the_list()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var stranger = await SignedUpAsync(services);

        foreach (var (target, actor) in new[] { (tenant, stranger), (TenantId.New(), owner) })
        {
            (await store.RenameAsync(target, actor, "Mine", Ct)).ShouldBe(MemberChangeStatus.NotFound);
            (await store.UpdateMemberAsync(Update(target, actor, owner, ["viewer"], isOwner: false), Ct)).ShouldBe(MemberChangeStatus.NotFound);
            (await store.UpdateMemberAsync(Update(target, actor, actor, ["viewer"]), Ct)).ShouldBe(MemberChangeStatus.NotFound);
            (await store.RemoveMemberAsync(Remove(target, actor, owner), Ct)).ShouldBe(MemberChangeStatus.NotFound);
            (await store.RemoveMemberAsync(Remove(target, actor, actor), Ct)).ShouldBe(MemberChangeStatus.NotFound);
            (await store.ListMembersAsync(target, actor, Ct)).ShouldBeNull();
        }

        (await store.FindMembershipAsync(owner, tenant, Ct))!.Name.ShouldBe("Acme");
        (await VersionAsync(services, tenant)).ShouldBe(0);
    }

    [Fact]
    public async Task An_owner_changes_a_member_s_roles_and_ownership()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);

        (await store.UpdateMemberAsync(Update(tenant, owner, member, ["planner", "viewer"]), Ct)).ShouldBe(MemberChangeStatus.Changed);
        var changed = (await store.FindMembershipAsync(member, tenant, Ct)).ShouldNotBeNull();
        changed.Roles.ShouldBe(["planner", "viewer"]);
        changed.IsOwner.ShouldBeFalse("roles alone leave ownership as it was");

        (await store.UpdateMemberAsync(Update(tenant, owner, member, roles: null, isOwner: true), Ct)).ShouldBe(MemberChangeStatus.Changed);
        var promoted = (await store.FindMembershipAsync(member, tenant, Ct)).ShouldNotBeNull();
        promoted.IsOwner.ShouldBeTrue();
        promoted.Roles.ShouldBe(["planner", "viewer"], "ownership alone leaves roles as they were");

        (await store.UpdateMemberAsync(Update(tenant, member, owner, ["dispatcher"], isOwner: false), Ct)).ShouldBe(MemberChangeStatus.Changed);
        var demoted = (await store.FindMembershipAsync(owner, tenant, Ct)).ShouldNotBeNull();
        demoted.IsOwner.ShouldBeFalse("a second owner demotes the first");
        demoted.Roles.ShouldBe(["dispatcher"]);
        (await OwnersAsync(services, tenant)).ShouldBe(1);
    }

    [Fact]
    public async Task Nobody_changes_their_own_membership()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);

        (await store.UpdateMemberAsync(Update(tenant, owner, owner, ["planner"], isOwner: false), Ct)).ShouldBe(MemberChangeStatus.Self);
        (await store.UpdateMemberAsync(Update(tenant, member, member, ["dispatcher"], isOwner: true), Ct)).ShouldBe(MemberChangeStatus.Self);

        Same((await store.FindMembershipAsync(owner, tenant, Ct))!, Membership(tenant, ["dispatcher"], isOwner: true, _now));
        Same((await store.FindMembershipAsync(member, tenant, Ct))!, Membership(tenant, ["viewer"], isOwner: false, _now.AddMinutes(1)));
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_removed_or_leave()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);

        (await store.RemoveMemberAsync(Remove(tenant, owner, owner), Ct)).ShouldBe(MemberChangeStatus.LastOwner, "leaving");
        (await store.UpdateMemberAsync(Update(tenant, owner, owner, roles: null, isOwner: false), Ct))
            .ShouldBe(MemberChangeStatus.Self, "demoting oneself is refused before ownership is judged");
        (await store.UpdateMemberAsync(Update(tenant, member, owner, roles: null, isOwner: false), Ct))
            .ShouldBe(MemberChangeStatus.NotOwner, "only an owner demotes, and the last owner has no fellow owner");
        (await store.RemoveMemberAsync(Remove(tenant, member, owner), Ct))
            .ShouldBe(MemberChangeStatus.NotOwner, "only an owner removes, and the last owner has no fellow owner");

        (await OwnersAsync(services, tenant)).ShouldBe(1);
        (await store.FindMembershipAsync(owner, tenant, Ct))!.IsOwner.ShouldBeTrue();
        (await store.ListMembersAsync(tenant, owner, Ct))!.Count.ShouldBe(2);
        (await VersionAsync(services, tenant)).ShouldBe(0, "every refusal rolled its bump back");
    }

    [Fact]
    public async Task A_second_owner_makes_leaving_possible()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        (await store.UpdateMemberAsync(Update(tenant, owner, member, roles: null, isOwner: true), Ct)).ShouldBe(MemberChangeStatus.Changed);

        (await store.RemoveMemberAsync(Remove(tenant, owner, owner), Ct)).ShouldBe(MemberChangeStatus.Changed);

        (await store.FindMembershipAsync(owner, tenant, Ct)).ShouldBeNull();
        (await store.FindMembershipAsync(member, tenant, Ct))!.IsOwner.ShouldBeTrue();
        (await store.RemoveMemberAsync(Remove(tenant, member, member), Ct)).ShouldBe(MemberChangeStatus.LastOwner, "now the last one");
    }

    [Fact]
    public async Task A_member_leaves_and_a_non_owner_cannot_remove_another()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var colleague = await SignedUpAsync(services);
        await TenantSeed.AddMemberAsync(services, tenant, colleague, [], isOwner: false, _now.AddMinutes(2));

        (await store.RemoveMemberAsync(Remove(tenant, member, colleague), Ct)).ShouldBe(MemberChangeStatus.NotOwner);
        (await store.RemoveMemberAsync(Remove(tenant, member, member), Ct)).ShouldBe(MemberChangeStatus.Changed, "a member leaves");
        (await store.RemoveMemberAsync(Remove(tenant, owner, member), Ct)).ShouldBe(MemberChangeStatus.NoSuchMember);
        (await store.UpdateMemberAsync(Update(tenant, owner, member, ["viewer"]), Ct)).ShouldBe(MemberChangeStatus.NoSuchMember);
        (await store.RemoveMemberAsync(Remove(tenant, owner, colleague), Ct)).ShouldBe(MemberChangeStatus.Changed, "an owner removes");

        (await store.ListMembersAsync(tenant, owner, Ct))!.Select(view => view.User).ShouldBe([owner]);
        (await store.ListMembersAsync(tenant, member, Ct)).ShouldBeNull("a former member is a stranger");
    }

    [Fact]
    public async Task A_personal_tenant_refuses_every_member_change()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);

        (await store.UpdateMemberAsync(Update(personal, owner, other, ["viewer"], isOwner: true), Ct)).ShouldBe(MemberChangeStatus.PersonalTenant);
        (await store.RemoveMemberAsync(Remove(personal, owner, owner), Ct)).ShouldBe(MemberChangeStatus.PersonalTenant);
        (await store.RemoveMemberAsync(Remove(personal, owner, other), Ct)).ShouldBe(MemberChangeStatus.PersonalTenant);

        var membership = (await store.FindMembershipAsync(owner, null, Ct)).ShouldNotBeNull();
        membership.Tenant.ShouldBe(personal);
        membership.IsOwner.ShouldBeTrue();
        (await store.ListMembersAsync(personal, owner, Ct))!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Members_list_display_names_and_avatars_from_the_accounts()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services, "Ada", "https://img/ada");
        var nameless = await SignedUpAsync(services, name: null);
        var gone = new UserId(Guid.Parse("00000000-0000-4000-8000-000000000001", CultureInfo.InvariantCulture));
        var tenant = TenantId.New();
        await store.CreateSharedAsync(Create(tenant, owner, ["dispatcher"]), Ct);
        await TenantSeed.AddMemberAsync(services, tenant, nameless, ["viewer"], isOwner: false, _now.AddMinutes(1));
        await TenantSeed.AddMemberAsync(services, tenant, gone, [], isOwner: false, _now.AddMinutes(1));

        var members = (await store.ListMembersAsync(tenant, nameless, Ct)).ShouldNotBeNull("any member lists the members");

        members.Count.ShouldBe(3);
        Same(members[0], new MemberView(owner, "Ada", "https://img/ada", ["dispatcher"], IsOwner: true, _now));
        var tied = Order(nameless, gone);
        Same(members[1], tied[0] == gone
            ? new MemberView(gone, null, null, [], IsOwner: false, _now.AddMinutes(1))
            : new MemberView(nameless, null, null, ["viewer"], IsOwner: false, _now.AddMinutes(1)));
        members[1].User.ShouldBe(tied[0], "a tie in joining is broken by account id");
        members[2].User.ShouldBe(tied[1]);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_once_leave_exactly_one_owner()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, first, second) = await TwoOwnersAsync(store, services);

            var statuses = await Task.WhenAll(
                store.UpdateMemberAsync(Update(tenant, first, second, roles: null, isOwner: false), Ct),
                store.UpdateMemberAsync(Update(tenant, second, first, roles: null, isOwner: false), Ct));

            statuses.ShouldBe([MemberChangeStatus.Changed, MemberChangeStatus.NotOwner], ignoreOrder: true, $"iteration {iteration}");
            (await OwnersAsync(services, tenant)).ShouldBe(1, $"iteration {iteration}");
        }
    }

    [Fact]
    public async Task Two_owners_removing_each_other_at_once_leave_exactly_one_owner()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, first, second) = await TwoOwnersAsync(store, services);

            var statuses = await Task.WhenAll(
                store.RemoveMemberAsync(Remove(tenant, first, second), Ct),
                store.RemoveMemberAsync(Remove(tenant, second, first), Ct));

            statuses.ShouldBe([MemberChangeStatus.Changed, MemberChangeStatus.NotFound], ignoreOrder: true, $"iteration {iteration}");
            var remaining = await Task.WhenAll(store.FindMembershipAsync(first, tenant, Ct), store.FindMembershipAsync(second, tenant, Ct));
            remaining.Count(membership => membership is { IsOwner: true }).ShouldBe(1, $"iteration {iteration}");
            remaining.Count(membership => membership is null).ShouldBe(1, $"iteration {iteration}");
        }
    }

    /// <summary>
    /// The one race whose loser reaches the owner count (spec §6.4): two owners leave at the same instant. The leaver that
    /// waited on the other's version bump re-counts the owners after it committed and finds itself the last one.
    /// </summary>
    [Fact]
    public async Task Two_owners_leaving_at_once_leave_exactly_one_owner()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, first, second) = await TwoOwnersAsync(store, services);

            var statuses = await Task.WhenAll(
                store.RemoveMemberAsync(Remove(tenant, first, first), Ct),
                store.RemoveMemberAsync(Remove(tenant, second, second), Ct));

            statuses.ShouldBe([MemberChangeStatus.Changed, MemberChangeStatus.LastOwner], ignoreOrder: true, $"iteration {iteration}");
            var remaining = await Task.WhenAll(store.FindMembershipAsync(first, tenant, Ct), store.FindMembershipAsync(second, tenant, Ct));
            remaining.Count(membership => membership is { IsOwner: true }).ShouldBe(1, $"iteration {iteration}");
            remaining.Count(membership => membership is null).ShouldBe(1, $"iteration {iteration}");
            (await OwnersAsync(services, tenant)).ShouldBe(1, $"iteration {iteration}");
        }
    }

    /// <summary>Starts a fresh database and returns its tenant store and container.</summary>
    private protected async Task<(ITenantStore Store, IServiceProvider Services)> StoreAsync()
    {
        var services = await StartAsync();
        return (services.GetRequiredService<ITenantStore>(), services);
    }

    /// <summary>Signs a fresh identity up and returns its account.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="name">The identity's display name.</param>
    /// <param name="picture">The identity's avatar URL.</param>
    private protected static async Task<UserId> SignedUpAsync(IServiceProvider services, string? name = "Ada", string? picture = null)
    {
        var identity = new VerifiedIdentity("google", "sub-" + Guid.NewGuid().ToString("N"), "ada@gmail.com", true, name, picture);
        var request = new SignInRequest(
            identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now.AddDays(180), null);
        var outcome = await services.GetRequiredService<IAccountStore>().SignInAsync(request, Ct);
        return outcome.Account!.Value;
    }

    /// <summary>A creation of a shared tenant named <c>Acme</c>.</summary>
    private protected static CreateTenantRequest Create(
        TenantId tenant, UserId creator, IReadOnlyList<string> roles, DateTimeOffset? now = null, int cap = 5) =>
        new(tenant, "Acme", creator, roles, cap, now ?? _now);

    /// <summary>A tenant id whose text sorts by <paramref name="order"/> — for the facts that pin a tie-break by id.</summary>
    private protected static TenantId Tenant(byte order) =>
        new(Guid.Parse(order.ToString("x2", CultureInfo.InvariantCulture) + "000000-0000-4000-8000-000000000000", CultureInfo.InvariantCulture));

    /// <summary>Asserts two memberships are equal, roles compared by value.</summary>
    private protected static void Same(StoredMembership actual, StoredMembership expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

    /// <summary>A shared tenant <c>Acme</c> whose owner holds <c>dispatcher</c>, and a member who holds <c>viewer</c> and joined a minute later.</summary>
    private protected static async Task<(TenantId Tenant, UserId Owner, UserId Member)> TeamAsync(ITenantStore store, IServiceProvider services)
    {
        var owner = await SignedUpAsync(services);
        var member = await SignedUpAsync(services);
        var tenant = TenantId.New();
        (await store.CreateSharedAsync(Create(tenant, owner, ["dispatcher"]), Ct)).Status.ShouldBe(CreateTenantStatus.Created);
        await TenantSeed.AddMemberAsync(services, tenant, member, ["viewer"], isOwner: false, _now.AddMinutes(1));
        return (tenant, owner, member);
    }

    /// <summary>A shared tenant with two owners and no other member.</summary>
    private protected static async Task<(TenantId Tenant, UserId First, UserId Second)> TwoOwnersAsync(ITenantStore store, IServiceProvider services)
    {
        var first = await SignedUpAsync(services);
        var second = await SignedUpAsync(services);
        var tenant = TenantId.New();
        (await store.CreateSharedAsync(Create(tenant, first, []), Ct)).Status.ShouldBe(CreateTenantStatus.Created);
        await TenantSeed.AddMemberAsync(services, tenant, second, [], isOwner: true, _now.AddMinutes(1));
        return (tenant, first, second);
    }

    /// <summary>A change of <paramref name="member"/> by <paramref name="actor"/>.</summary>
    private protected static MemberUpdate Update(
        TenantId tenant, UserId actor, UserId member, IReadOnlyList<string>? roles, bool? isOwner = null) =>
        new(tenant, actor, member, roles, isOwner, _now.AddHours(1));

    /// <summary>A removal of <paramref name="member"/> by <paramref name="actor"/>.</summary>
    private protected static MemberRemoval Remove(TenantId tenant, UserId actor, UserId member) =>
        new(tenant, actor, member, _now.AddHours(1));

    /// <summary>A membership of the <c>Acme</c> shared tenant.</summary>
    private protected static StoredMembership Membership(TenantId tenant, IReadOnlyList<string> roles, bool isOwner, DateTimeOffset joinedAt) =>
        new(tenant, TenantKind.Shared, "Acme", roles, isOwner, joinedAt);

    /// <summary>Asserts two member views are equal, roles compared by value.</summary>
    private protected static void Same(MemberView actual, MemberView expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

    /// <summary>The two accounts in the order their stored ids sort.</summary>
    private protected static UserId[] Order(UserId first, UserId second) =>
        [.. new[] { first, second }.OrderBy(user => user.Value.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)];

    /// <summary>How many owners <paramref name="tenant"/> has.</summary>
    protected static async Task<long> OwnersAsync(IServiceProvider services, TenantId tenant) => Convert.ToInt64(
        await ScalarAsync(
            services,
            $"SELECT COUNT(*) FROM {TableOf(services, AlvoFrameworkTables.TenantMembersSuffix)} WHERE tenant_id = '{tenant.Value:D}' AND is_owner = 1"),
        CultureInfo.InvariantCulture);

    /// <summary>The prefixed name of the framework table <paramref name="suffix"/> names.</summary>
    protected static string TableOf(IServiceProvider services, string suffix) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix;

    /// <summary>How many rows the framework table <paramref name="suffix"/> names holds.</summary>
    protected static async Task<long> CountAsync(IServiceProvider services, string suffix) =>
        Convert.ToInt64(await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, suffix)}"), CultureInfo.InvariantCulture);

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
