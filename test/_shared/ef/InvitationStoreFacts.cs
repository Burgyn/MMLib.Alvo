using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore;
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
/// The tenant registry's invitations — creating, listing and revoking them, an owner's invitations dying with their
/// ownership, previewing one and accepting one — the same facts on SQLite and on PostgreSQL (accounts spec §5.3, §6.1,
/// §6.4; S4 plan D2, D3, D4, D6, D8, D12, D15). Accounts are
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

    [Fact]
    public async Task Accepting_makes_a_member_with_the_invitation_s_roles_ownership_and_provenance()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var (planner, coOwner) = (await SignedUpAsync(services), await SignedUpAsync(services));
        var (asPlanner, asOwner) = (Invite(tenant, owner, ["planner", "viewer"]), Invite(tenant, owner, ["dispatcher"], grantsOwner: true));
        await store.CreateInvitationAsync(asPlanner, Ct);
        await store.CreateInvitationAsync(asOwner, Ct);

        var joined = await AcceptAsync(store, asPlanner.CodeHash, planner);
        var promoted = await AcceptAsync(store, asOwner.CodeHash, coOwner);

        var expected = new StoredMembership(tenant, TenantKind.Shared, "Acme", ["planner", "viewer"], IsOwner: false, _now.AddHours(1));
        joined.Status.ShouldBe(AcceptInvitationStatus.Joined);
        Same(joined.Membership.ShouldNotBeNull(), expected);
        Same((await store.FindMembershipAsync(planner, tenant, Ct)).ShouldNotBeNull(), expected);
        Same((await store.FindMembershipAsync(coOwner, tenant, Ct)).ShouldNotBeNull(), expected with { Roles = ["dispatcher"], IsOwner = true });
        promoted.Membership.ShouldNotBeNull().IsOwner.ShouldBeTrue("grants_owner makes an owner");
        (await TenantSeed.InvitationIdOfAsync(services, tenant, planner)).ShouldBe(asPlanner.Id, "the membership's provenance");
        (await TenantSeed.InvitationIdOfAsync(services, tenant, owner)).ShouldBeNull("a creator joined through no invitation");
        (await UsesAsync(services, asPlanner.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Accepting_twice_is_already_a_member_and_consumes_nothing()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var invitee = await SignedUpAsync(services);
        var request = Invite(tenant, owner, ["viewer"], maxUses: 3);
        await store.CreateInvitationAsync(request, Ct);
        (await AcceptAsync(store, request.CodeHash, invitee)).Status.ShouldBe(AcceptInvitationStatus.Joined);
        (await store.UpdateMemberAsync(Update(tenant, owner, invitee, ["planner"], isOwner: null), Ct)).ShouldBe(MemberChangeStatus.Changed);

        var again = await AcceptAsync(store, request.CodeHash, invitee, _now.AddHours(2));
        var existing = await AcceptAsync(store, request.CodeHash, member, _now.AddHours(2));

        again.Status.ShouldBe(AcceptInvitationStatus.AlreadyMember);
        again.Membership.ShouldNotBeNull().Roles.ShouldBe(["planner"], "the roles an owner gave stay");
        existing.Status.ShouldBe(AcceptInvitationStatus.AlreadyMember, "a member from before the invitation is one too");
        existing.Membership.ShouldNotBeNull().Roles.ShouldBe(["viewer"]);
        (await UsesAsync(services, request.Id)).ShouldBe(1, "being a member consumes no use");
        (await store.FindMembershipAsync(invitee, tenant, Ct)).ShouldNotBeNull().Roles.ShouldBe(["planner"]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired at the instant")]
    [InlineData("expired after")]
    [InlineData("revoked")]
    [InlineData("used up")]
    [InlineData("creator demoted")]
    [InlineData("creator removed")]
    [InlineData("creator no owner")]
    [InlineData("bound to another email")]
    public async Task Every_dead_invitation_previews_and_accepts_as_dead(string reason)
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var person = await SignedUpAsync(services);
        var (hash, now) = await DeadAsync(store, services, reason, tenant, owner, member);

        (await store.PreviewInvitationAsync(hash, now, Ct) is null)
            .ShouldBe(reason != "bound to another email", $"{reason}: an anonymous preview judges no binding (D8)");
        (await AcceptAsync(store, hash, person, now)).ShouldBe(new AcceptInvitationOutcome(AcceptInvitationStatus.Dead, null), reason);
        (await store.FindMembershipAsync(person, tenant, Ct)).ShouldBeNull(reason);
    }

    /// <summary>
    /// A member of the tenant who accepts one of its invitations is already a member, whatever state the invitation is in:
    /// membership is judged before liveness, so a double submit of a single-use code answers a membership both times. The
    /// stored membership is unchanged and no use is consumed. A code nobody issued is still dead.
    /// </summary>
    /// <param name="reason">Why the invitation is dead.</param>
    [Theory]
    [InlineData("expired at the instant")]
    [InlineData("expired after")]
    [InlineData("revoked")]
    [InlineData("used up")]
    [InlineData("creator demoted")]
    [InlineData("creator removed")]
    [InlineData("creator no owner")]
    [InlineData("bound to another email")]
    public async Task A_member_accepting_a_dead_invitation_of_their_tenant_is_already_a_member(string reason)
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, member) = await TeamAsync(store, services);
        var (hash, now) = await DeadAsync(store, services, reason, tenant, owner, member);
        var before = (await store.FindMembershipAsync(member, tenant, Ct)).ShouldNotBeNull(reason);
        var uses = await ScalarAsync(services, $"SELECT SUM(uses) FROM {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)}");

        var outcome = await AcceptAsync(store, hash, member, now);

        outcome.Status.ShouldBe(AcceptInvitationStatus.AlreadyMember, reason);
        Same(outcome.Membership.ShouldNotBeNull(reason), before);
        Same((await store.FindMembershipAsync(member, tenant, Ct)).ShouldNotBeNull(reason), before);
        (await ScalarAsync(services, $"SELECT SUM(uses) FROM {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)}"))
            .ShouldBe(uses, reason);
    }

    [Fact]
    public async Task A_member_presenting_a_code_nobody_issued_is_dead()
    {
        var (store, services) = await StoreAsync();
        var (_, _, member) = await TeamAsync(store, services);

        (await AcceptAsync(store, Hash(), member)).ShouldBe(new AcceptInvitationOutcome(AcceptInvitationStatus.Dead, null));
    }

    [Fact]
    public async Task Accepting_a_single_use_invitation_again_is_already_a_member()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var invitee = await SignedUpAsync(services);
        var request = Invite(tenant, owner, ["viewer"]);
        await store.CreateInvitationAsync(request, Ct);

        var first = await AcceptAsync(store, request.CodeHash, invitee);
        var again = await AcceptAsync(store, request.CodeHash, invitee, _now.AddHours(2));

        first.Status.ShouldBe(AcceptInvitationStatus.Joined);
        again.Status.ShouldBe(AcceptInvitationStatus.AlreadyMember, "the double submit of a used-up code still answers a membership");
        Same(again.Membership.ShouldNotBeNull(), first.Membership.ShouldNotBeNull());
        (await UsesAsync(services, request.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task An_invitation_into_a_personal_tenant_is_dead_even_when_its_row_exists()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services);
        var stranger = await SignedUpAsync(services);
        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);
        var request = Invite(personal, owner, maxUses: 5);
        await TenantSeed.AddInvitationAsync(services, request);

        (await store.PreviewInvitationAsync(request.CodeHash, _now, Ct)).ShouldBeNull();
        (await AcceptAsync(store, request.CodeHash, stranger)).Status.ShouldBe(AcceptInvitationStatus.Dead);
        (await store.FindMembershipAsync(stranger, personal, Ct)).ShouldBeNull();
        (await UsesAsync(services, request.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task An_email_bound_invitation_admits_only_that_trusted_email()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var (first, second) = (Invite(tenant, owner, email: "ada@gmail.com", maxUses: 5), Invite(tenant, owner, email: "ada@gmail.com"));
        await store.CreateInvitationAsync(first, Ct);
        await store.CreateInvitationAsync(second, Ct);
        var untrusted = await SignedUpAsync(services, "ada@gmail.com", trusted: false);
        var another = await SignedUpAsync(services, "bob@gmail.com");
        var none = await SignedUpAsync(services, email: null, trusted: false);

        (await AcceptAsync(store, first.CodeHash, untrusted)).Status.ShouldBe(AcceptInvitationStatus.Dead, "an untrusted email satisfies nothing");
        (await AcceptAsync(store, first.CodeHash, another)).Status.ShouldBe(AcceptInvitationStatus.Dead, "another address");
        (await AcceptAsync(store, first.CodeHash, none)).Status.ShouldBe(AcceptInvitationStatus.Dead, "no email at all");
        (await AcceptAsync(store, first.CodeHash, await SignedUpAsync(services, "ada@gmail.com"))).Status.ShouldBe(AcceptInvitationStatus.Joined);
        (await AcceptAsync(store, second.CodeHash, await SignedUpAsync(services, "ADA@Gmail.com"))).Status
            .ShouldBe(AcceptInvitationStatus.Joined, "the binding ignores case");

        (await UsesAsync(services, first.Id)).ShouldBe(1, "a refused binding consumes nothing");
    }

    [Fact]
    public async Task A_disabled_or_unknown_person_accepts_nothing()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var disabled = await SignedUpAsync(services);
        (await services.GetRequiredService<IAccountStore>().SetDisabledAsync(disabled, disabled: true, _now, Ct)).ShouldBeTrue();
        var request = Invite(tenant, owner, maxUses: 5);
        await store.CreateInvitationAsync(request, Ct);

        (await AcceptAsync(store, request.CodeHash, disabled)).ShouldBe(new AcceptInvitationOutcome(AcceptInvitationStatus.NoAccount, null));
        (await AcceptAsync(store, request.CodeHash, UserId.New())).ShouldBe(new AcceptInvitationOutcome(AcceptInvitationStatus.NoAccount, null));

        (await store.FindMembershipAsync(disabled, tenant, Ct)).ShouldBeNull();
        (await UsesAsync(services, request.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Preview_names_the_tenant_the_inviter_the_roles_and_whether_an_email_is_bound()
    {
        var (store, services) = await StoreAsync();
        var grace = await SignedUpAsync(services, name: "Grace");
        var nameless = await SignedUpAsync(services, name: null);
        var tenant = TenantId.New();
        await store.CreateSharedAsync(new CreateTenantRequest(tenant, "Acme", grace, [], 5, _now), Ct);
        await TenantSeed.AddMemberAsync(services, tenant, nameless, [], isOwner: true, _now);
        var bound = Invite(tenant, grace, ["planner", "viewer"], grantsOwner: true, email: "bob@example.com");
        var open = Invite(tenant, nameless, ["viewer"]);
        await store.CreateInvitationAsync(bound, Ct);
        await store.CreateInvitationAsync(open, Ct);

        var named = (await store.PreviewInvitationAsync(bound.CodeHash, _now, Ct)).ShouldNotBeNull();
        var anonymous = (await store.PreviewInvitationAsync(open.CodeHash, _now, Ct)).ShouldNotBeNull();

        Same(named, new InvitationPreview(tenant, "Acme", "Grace", ["planner", "viewer"], GrantsOwner: true, EmailBound: true, _now.AddDays(7)));
        Same(anonymous, new InvitationPreview(tenant, "Acme", null, ["viewer"], GrantsOwner: false, EmailBound: false, _now.AddDays(7)));
        (await UsesAsync(services, bound.Id)).ShouldBe(0, "a preview consumes nothing");
    }

    /// <summary>
    /// Review Focus 1 (S4 plan D4): two people accept a single-use invitation at the same instant. Step 5's conditional
    /// update is the decision — exactly one matches, the other finds the invitation used up and is dead.
    /// </summary>
    [Fact]
    public async Task Two_people_accepting_a_single_use_invitation_at_once_make_one_member()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var request = Invite(tenant, owner);
            await store.CreateInvitationAsync(request, Ct);
            var (first, second) = (await SignedUpAsync(services), await SignedUpAsync(services));

            var (one, two) = await RaceAsync(() => AcceptAsync(store, request.CodeHash, first), () => AcceptAsync(store, request.CodeHash, second));

            new[] { one.Status, two.Status }.ShouldBe([AcceptInvitationStatus.Joined, AcceptInvitationStatus.Dead], ignoreOrder: true, $"iteration {iteration}");
            (await MembersJoinedAsync(store, tenant, first, second)).ShouldBe(1, $"iteration {iteration}");
            (await UsesAsync(services, request.Id)).ShouldBe(1, $"iteration {iteration}");
        }
    }

    [Fact]
    public async Task Five_people_accepting_a_three_use_invitation_at_once_make_three_members()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);

        for (var iteration = 0; iteration < Iterations / 2; iteration++)
        {
            var request = Invite(tenant, owner, maxUses: 3);
            await store.CreateInvitationAsync(request, Ct);
            var people = new UserId[5];
            for (var index = 0; index < people.Length; index++)
            {
                people[index] = await SignedUpAsync(services);
            }

            var outcomes = await AllAtOnceAsync(people.Select(person => (Func<Task<AcceptInvitationOutcome>>)(() => AcceptAsync(store, request.CodeHash, person))));

            outcomes.Count(outcome => outcome.Status == AcceptInvitationStatus.Joined).ShouldBe(3, $"iteration {iteration}");
            outcomes.Count(outcome => outcome.Status == AcceptInvitationStatus.Dead).ShouldBe(2, $"iteration {iteration}");
            (await MembersJoinedAsync(store, tenant, people)).ShouldBe(3, $"iteration {iteration}");
            (await UsesAsync(services, request.Id)).ShouldBe(3, $"iteration {iteration}");
        }
    }

    /// <summary>
    /// Review Focus 3 (S4 plan D4 step 1): one person submits the same accept twice at once. The account row lock
    /// serialises the two, so the second sees the first's membership: one join, one use, both answers a membership — for
    /// a single-use invitation too, which the first accept used up, because membership is judged before liveness.
    /// </summary>
    /// <param name="maxUses">The invitation's use count.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task One_person_accepting_twice_at_once_joins_once(int maxUses)
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var request = Invite(tenant, owner, maxUses: maxUses);
            await store.CreateInvitationAsync(request, Ct);
            var person = await SignedUpAsync(services);

            var (one, two) = await RaceAsync(() => AcceptAsync(store, request.CodeHash, person), () => AcceptAsync(store, request.CodeHash, person));

            new[] { one.Status, two.Status }.ShouldBe([AcceptInvitationStatus.Joined, AcceptInvitationStatus.AlreadyMember], ignoreOrder: true, $"iteration {iteration}");
            one.Membership.ShouldNotBeNull($"iteration {iteration}");
            two.Membership.ShouldNotBeNull($"iteration {iteration}");
            (await UsesAsync(services, request.Id)).ShouldBe(1, $"iteration {iteration}");
        }
    }

    /// <summary>
    /// Review Focus 2 (S4 plan D4, D6): a person accepts while the invitation's creator is demoted. The demotion's sweep and
    /// the acceptance's conditional update meet on the invitation's row, so either the person joined before the sweep —
    /// which then revoked the invitation — or the acceptance found it revoked, or its creator no owner, and is dead.
    /// </summary>
    [Fact]
    public async Task An_accept_racing_its_creator_s_demotion_never_joins_after_the_sweep()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var (tenant, first, second) = await TwoOwnersAsync(store, services);
            var request = Invite(tenant, first, maxUses: 5);
            await store.CreateInvitationAsync(request, Ct);
            var person = await SignedUpAsync(services);

            var (accepted, demoted) = await RaceAsync(
                async () => { await Task.Delay(iteration % 4, Ct); return await AcceptAsync(store, request.CodeHash, person); },
                () => store.UpdateMemberAsync(Update(tenant, second, first, roles: null, isOwner: false), Ct));

            demoted.ShouldBe(MemberChangeStatus.Changed, $"iteration {iteration}");
            accepted.Status.ShouldBeOneOf([AcceptInvitationStatus.Joined, AcceptInvitationStatus.Dead], $"iteration {iteration}");
            (await RevokedAtAsync(store, tenant, request.Id)).ShouldNotBeNull($"iteration {iteration}: the sweep revoked it");
            (await store.FindMembershipAsync(person, tenant, Ct) is not null)
                .ShouldBe(accepted.Status == AcceptInvitationStatus.Joined, $"iteration {iteration}");
            (await UsesAsync(services, request.Id)).ShouldBe(accepted.Status == AcceptInvitationStatus.Joined ? 1 : 0, $"iteration {iteration}");
        }
    }

    /// <summary>
    /// The deterministic half of Review Focus 2: an acceptance reads the invitation live while a revocation of it — the
    /// stand-in for a demotion's sweep — holds its row uncommitted, then reaches the conditional update only after the
    /// revocation committed. Step 5's own <c>revoked_at IS NULL</c> is what refuses it: step 3's read was already past.
    /// (On SQLite the open revocation holds the write lock, so the acceptance starts only after it and reads it dead.)
    /// </summary>
    [Fact]
    public async Task An_accept_that_waited_on_a_revocation_finds_the_invitation_revoked()
    {
        var (store, services) = await StoreAsync();
        var (tenant, owner, _) = await TeamAsync(store, services);
        var request = Invite(tenant, owner, maxUses: 5);
        await store.CreateInvitationAsync(request, Ct);
        var person = await SignedUpAsync(services);

        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using var revocation = await connection.BeginTransactionAsync(Ct);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = revocation;
            command.CommandText =
                $"UPDATE {TableOf(services, AlvoFrameworkTables.TenantInvitationsSuffix)} SET revoked_at = '{StoredInstant.Text(_now)}' WHERE id = '{request.Id:D}'";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var accepting = Task.Run(() => AcceptAsync(store, request.CodeHash, person), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        await revocation.CommitAsync(Ct);

        (await accepting).ShouldBe(new AcceptInvitationOutcome(AcceptInvitationStatus.Dead, null));
        (await store.FindMembershipAsync(person, tenant, Ct)).ShouldBeNull();
        (await UsesAsync(services, request.Id)).ShouldBe(0);
    }

    /// <summary>Builds the dead invitation <paramref name="reason"/> names and returns its hash and the instant to try it at.</summary>
    private protected static async Task<(string Hash, DateTimeOffset Now)> DeadAsync(
        ITenantStore store, IServiceProvider services, string reason, TenantId tenant, UserId owner, UserId member)
    {
        var request = Invite(tenant, owner, maxUses: 2);
        var later = _now.AddHours(1);
        switch (reason)
        {
            case "unknown":
                return (Hash(), later);
            case "creator no owner":
                await TenantSeed.AddInvitationAsync(services, Invite(tenant, member, maxUses: 2) with { CodeHash = request.CodeHash });
                return (request.CodeHash, later);
            case "bound to another email":
                await store.CreateInvitationAsync(request with { Email = "bob@example.com" }, Ct);
                return (request.CodeHash, later);
        }

        (await store.CreateInvitationAsync(request, Ct)).Status.ShouldBe(MemberChangeStatus.Changed);
        return reason switch
        {
            "expired at the instant" => (request.CodeHash, request.ExpiresAt),
            "expired after" => (request.CodeHash, request.ExpiresAt.AddTicks(10)),
            _ => (request.CodeHash, await KillAsync(store, services, reason, request, member)),
        };
    }

    /// <summary>Kills a live invitation by <paramref name="reason"/> and returns the instant to try it at.</summary>
    private protected static async Task<DateTimeOffset> KillAsync(
        ITenantStore store, IServiceProvider services, string reason, CreateInvitationRequest request, UserId member)
    {
        var (tenant, owner, later) = (request.Tenant, request.Actor, _now.AddHours(1));
        if (reason == "revoked")
        {
            (await store.RevokeInvitationAsync(Revoke(tenant, owner, request.Id), Ct)).ShouldBe(MemberChangeStatus.Changed);
            return later;
        }

        if (reason == "used up")
        {
            (await AcceptAsync(store, request.CodeHash, await SignedUpAsync(services))).Status.ShouldBe(AcceptInvitationStatus.Joined);
            (await AcceptAsync(store, request.CodeHash, await SignedUpAsync(services))).Status.ShouldBe(AcceptInvitationStatus.Joined);
            return later;
        }

        (await store.UpdateMemberAsync(Update(tenant, owner, member, roles: null, isOwner: true), Ct)).ShouldBe(MemberChangeStatus.Changed);
        var status = reason == "creator demoted"
            ? await store.UpdateMemberAsync(Update(tenant, member, owner, roles: null, isOwner: false), Ct)
            : await store.RemoveMemberAsync(new MemberRemoval(tenant, member, owner, later), Ct);
        status.ShouldBe(MemberChangeStatus.Changed, reason);
        return later;
    }

    /// <summary>One acceptance of the invitation whose code hashes to <paramref name="hash"/>, an hour after the start by default.</summary>
    private protected static Task<AcceptInvitationOutcome> AcceptAsync(ITenantStore store, string hash, UserId person, DateTimeOffset? now = null) =>
        store.AcceptInvitationAsync(new AcceptInvitationRequest(hash, person, now ?? _now.AddHours(1)), Ct);

    /// <summary>How many of <paramref name="people"/> are members of <paramref name="tenant"/>.</summary>
    private protected static async Task<int> MembersJoinedAsync(ITenantStore store, TenantId tenant, params UserId[] people)
    {
        var memberships = await Task.WhenAll(people.Select(person => store.FindMembershipAsync(person, tenant, Ct)));
        return memberships.Count(membership => membership is not null);
    }

    /// <summary>Runs every operation at once, released together, and returns their results.</summary>
    private protected static async Task<TResult[]> AllAtOnceAsync<TResult>(IEnumerable<Func<Task<TResult>>> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = operations.Select(operation => Task.Run(async () => { await start.Task; return await operation(); }, Ct)).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    /// <summary>The stored <c>uses</c> of one invitation.</summary>
    private protected static async Task<long> UsesAsync(IServiceProvider services, Guid invitation) =>
        Convert.ToInt64(await ColumnAsync(services, invitation, "uses"), CultureInfo.InvariantCulture);

    /// <summary>Asserts two memberships are equal, roles compared by value.</summary>
    private protected static void Same(StoredMembership actual, StoredMembership expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

    /// <summary>Asserts two previews are equal, roles compared by value.</summary>
    private protected static void Same(InvitationPreview actual, InvitationPreview expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

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
