using MMLib.Alvo.Accounts;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// The membership rules (S3 plan D4, D5; spec §5.7 step 3): what a request reads and what the membership found means —
/// default-deny without <c>selfService</c> or <c>personalTenant</c>, never a principal without the tenant it named, and
/// effective roles recomputed from the applied descriptor rather than trusted from the row.
/// </summary>
public sealed class MembershipRulesTests
{
    private static readonly Role _dispatcher = Role.Application("dispatcher");

    private static readonly Role _technician = Role.Application("technician");

    private static readonly TenantId _named = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));

    private static readonly TenantId _personalId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));

    private static readonly SelfServicePolicy _off = SelfServicePolicy.None;

    private static readonly SelfServicePolicy _shared = Policy(personal: null);

    private static readonly SelfServicePolicy _withPersonal = Policy(personal: [_technician]);

    private static SelfServicePolicy Policy(HashSet<Role>? personal) =>
        new(true, new HashSet<Role> { _dispatcher, _technician }, personal, new HashSet<Role> { _dispatcher }, 5);

    private static StoredMembership Shared(TenantId tenant, params string[] roles) =>
        new(tenant, TenantKind.Shared, "Acme", roles, IsOwner: false, DateTimeOffset.UnixEpoch);

    private static StoredMembership Personal(params string[] roles) =>
        new(_personalId, TenantKind.Personal, "Ada", roles, IsOwner: true, DateTimeOffset.UnixEpoch);

    private static StoredMembership PersonalNotOwned() => Personal() with { IsOwner = false };

    private static string[] Names(MembershipResolution resolution) =>
        [.. resolution.TenantRoles.Select(role => role.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void Without_self_service_a_named_tenant_is_refused_and_none_is_no_tenant_with_no_read()
    {
        MembershipRules.Plan(_named, _off).ShouldBe(new MembershipPlan(MembershipPlanKind.Refuse, null));
        MembershipRules.Plan(null, _off).ShouldBe(new MembershipPlan(MembershipPlanKind.NoTenant, null));
    }

    [Fact]
    public void With_self_service_a_named_tenant_reads_that_membership()
    {
        MembershipRules.Plan(_named, _shared).ShouldBe(new MembershipPlan(MembershipPlanKind.Read, new MembershipQuery(_named)));
        MembershipRules.Plan(_named, _withPersonal).ShouldBe(new MembershipPlan(MembershipPlanKind.Read, new MembershipQuery(_named)));
    }

    [Fact]
    public void Without_a_header_the_personal_tenant_is_read_only_while_the_descriptor_gives_one()
    {
        MembershipRules.Plan(null, _withPersonal).ShouldBe(new MembershipPlan(MembershipPlanKind.Read, new MembershipQuery(null)));
        MembershipRules.Plan(null, _shared).ShouldBe(new MembershipPlan(MembershipPlanKind.NoTenant, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_membership_refuses_a_named_tenant_and_is_no_tenant_without_one(bool personal)
    {
        var policy = personal ? _withPersonal : _shared;

        MembershipRules.Decide(_named, null, policy).ShouldBeSameAs(MembershipResolution.Refusal);
        MembershipRules.Decide(null, null, policy).ShouldBeSameAs(MembershipResolution.NoTenant);
    }

    [Fact]
    public void A_shared_membership_acts_in_its_tenant_with_its_assignable_roles()
    {
        var resolution = MembershipRules.Decide(_named, Shared(_named, "dispatcher"), _shared);

        resolution.Refused.ShouldBeFalse();
        resolution.Tenant.ShouldBe(_named);
        Names(resolution).ShouldBe(["dispatcher"]);
    }

    /// <summary>
    /// A stored name counts only while the applied <c>assignableRoles</c> lists it (D5): a role dropped from it, a project
    /// role, a built-in and an unknown name are never tenant roles, whatever the row holds.
    /// </summary>
    [Fact]
    public void A_stored_role_no_longer_assignable_is_dropped()
    {
        var narrowed = new SelfServicePolicy(true, new HashSet<Role> { _technician }, null, null, 5);

        var resolution = MembershipRules.Decide(
            _named, Shared(_named, "dispatcher", "technician", "admin", "authenticated", "owner", "ghost"), narrowed);

        resolution.Tenant.ShouldBe(_named);
        Names(resolution).ShouldBe(["technician"]);
    }

    [Fact]
    public void Personal_roles_come_from_the_policy_whatever_the_row_holds()
    {
        var resolution = MembershipRules.Decide(null, Personal("dispatcher"), _withPersonal);
        var named = MembershipRules.Decide(_personalId, Personal(), _withPersonal);

        resolution.Tenant.ShouldBe(_personalId);
        Names(resolution).ShouldBe(["technician"]);
        named.Tenant.ShouldBe(_personalId);
        Names(named).ShouldBe(["technician"]);
    }

    /// <summary>
    /// Defence in depth: a personal tenant's only member is its owner, so a personal row that is not the owner's — one the
    /// registry should never hold — lends nobody the personal roles; it is no membership.
    /// </summary>
    [Fact]
    public void Only_a_personal_tenant_s_owner_holds_its_roles()
    {
        MembershipRules.Decide(_personalId, PersonalNotOwned(), _withPersonal).ShouldBeSameAs(MembershipResolution.Refusal);
        MembershipRules.Decide(null, PersonalNotOwned(), _withPersonal).ShouldBeSameAs(MembershipResolution.NoTenant);
        MembershipRules.Decide(null, Personal(), _withPersonal).TenantRoles.ShouldBe([_technician]);
    }

    [Fact]
    public void A_personal_membership_is_not_honoured_without_personal_tenant()
    {
        MembershipRules.Decide(_personalId, Personal(), _shared).ShouldBeSameAs(MembershipResolution.Refusal);
        MembershipRules.Decide(null, Personal(), _shared).ShouldBeSameAs(MembershipResolution.NoTenant);
    }

    [Fact]
    public void Without_self_service_no_membership_is_honoured()
    {
        MembershipRules.Decide(_named, Shared(_named, "dispatcher"), _off).ShouldBeSameAs(MembershipResolution.Refusal);
        MembershipRules.Decide(null, Personal(), _off).ShouldBeSameAs(MembershipResolution.NoTenant);
    }

    /// <summary>
    /// A membership of another tenant never answers the one named, and a shared one never stands in for the personal one —
    /// a reader that returned the wrong row still cannot widen a principal.
    /// </summary>
    [Fact]
    public void A_membership_that_does_not_answer_the_request_is_not_honoured()
    {
        var other = new TenantId(Guid.Parse("33333333-3333-4333-8333-333333333333"));

        MembershipRules.Decide(_named, Shared(other, "dispatcher"), _withPersonal).ShouldBeSameAs(MembershipResolution.Refusal);
        MembershipRules.Decide(null, Shared(_named, "dispatcher"), _withPersonal).ShouldBeSameAs(MembershipResolution.NoTenant);
    }

    /// <summary>
    /// One test for "does this count": a tenant's kind under the policy, and a membership's on top of it — a personal one only
    /// as its owner's. The default tenant list and every membership listing read the same answers a request does.
    /// </summary>
    [Fact]
    public void Honours_is_one_test_for_a_tenant_and_its_membership()
    {
        (MembershipRules.Honours(TenantKind.Shared, _off), MembershipRules.Honours(TenantKind.Personal, _off)).ShouldBe((false, false));
        (MembershipRules.Honours(TenantKind.Shared, _shared), MembershipRules.Honours(TenantKind.Personal, _shared)).ShouldBe((true, false));
        (MembershipRules.Honours(TenantKind.Shared, _withPersonal), MembershipRules.Honours(TenantKind.Personal, _withPersonal))
            .ShouldBe((true, true));
        MembershipRules.Honours(Personal(), _withPersonal).ShouldBeTrue();
        MembershipRules.Honours(PersonalNotOwned(), _withPersonal).ShouldBeFalse("a personal row is honoured only as its owner's");
        MembershipRules.Honours(Shared(_named) with { IsOwner = false }, _withPersonal).ShouldBeTrue("a shared member need not own it");
        MembershipRules.Honours(Personal(), _shared).ShouldBeFalse();
    }

    /// <summary>
    /// <c>GET /user</c>'s memberships, <c>GET /tenants</c> and every route count a membership through the request's own test
    /// (review M2): a personal row that is not its owner's — one the registry should never hold — is listed nowhere.
    /// </summary>
    [Fact]
    public async Task A_listing_drops_a_personal_row_that_is_not_its_owner_s()
    {
        var user = UserId.New();
        var store = new MembershipsOnly(user, PersonalNotOwned(), Shared(_named, "dispatcher"));

        var listed = await SelfServiceMemberships.ListAsync(store, _withPersonal, user, TestContext.Current.CancellationToken);

        listed.ShouldHaveSingleItem().TenantId.ShouldBe(_named.Value);
    }

    /// <summary>A registry whose only answer is one person's memberships.</summary>
    private sealed class MembershipsOnly(UserId member, params StoredMembership[] memberships) : ITenantStore
    {
        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredMembership>>(user == member ? memberships : []);

        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) => throw new NotSupportedException();

        public Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
