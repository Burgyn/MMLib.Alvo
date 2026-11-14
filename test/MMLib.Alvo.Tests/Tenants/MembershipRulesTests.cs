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
}
