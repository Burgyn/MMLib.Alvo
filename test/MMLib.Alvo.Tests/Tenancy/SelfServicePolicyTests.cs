using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tenancy.SelfService;

namespace MMLib.Alvo.Tests.Tenancy;

/// <summary>
/// The self-service policy rides on the compiled catalog, so every membership read judges the descriptor that is
/// applied: the tenant roles, what a personal tenant and a creator hold, and the cap on shared tenants.
/// </summary>
public sealed class SelfServicePolicyTests
{
    private static PolicyCatalog Compile(string? selfService)
    {
        var tenancy = selfService is null ? string.Empty : $$""" "tenancy": { "enabled": true, "selfService": {{selfService}} },""";
        var descriptor = AlvoDescriptor.Parse($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "policy",
              "auth": { "roles": ["dispatcher", "technician", "clerk"] },
              {{tenancy}}
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    private static string[] Names(IEnumerable<Role>? roles) =>
        [.. (roles ?? []).Select(role => role.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void The_policy_carries_assignable_personal_and_creator_roles_and_the_cap()
    {
        var policy = Compile("""
            {
              "personalTenant": { "roles": ["technician"] },
              "createTenants": { "creatorRoles": ["dispatcher", "technician"], "maxPerAccount": 12 },
              "assignableRoles": ["dispatcher", "technician"]
            }
            """).SelfService;

        policy.Enabled.ShouldBeTrue();
        Names(policy.AssignableRoles).ShouldBe(["dispatcher", "technician"]);
        Names(policy.PersonalTenantRoles).ShouldBe(["technician"]);
        Names(policy.CreatorRoles).ShouldBe(["dispatcher", "technician"]);
        policy.MaxPerAccount.ShouldBe(12);
        policy.HasPersonalTenant.ShouldBeTrue();
        policy.CreatesTenants.ShouldBeTrue();
    }

    [Fact]
    public void Max_per_account_defaults_to_five()
    {
        var policy = Compile("""{ "createTenants": {}, "assignableRoles": ["dispatcher"] }""").SelfService;

        policy.MaxPerAccount.ShouldBe(5);
        policy.CreatesTenants.ShouldBeTrue();
        policy.CreatorRoles.ShouldNotBeNull().ShouldBeEmpty();
        policy.HasPersonalTenant.ShouldBeFalse();
    }

    /// <summary>A personal tenant without <c>roles</c> holds none (spec §3.1's default <c>[]</c>), and still exists.</summary>
    [Fact]
    public void A_personal_tenant_without_roles_holds_none()
    {
        var policy = Compile("""{ "personalTenant": {} }""").SelfService;

        policy.HasPersonalTenant.ShouldBeTrue();
        policy.PersonalTenantRoles.ShouldNotBeNull().ShouldBeEmpty();
        policy.CreatesTenants.ShouldBeFalse();
    }

    /// <summary>
    /// <b>A stored role counts only while the applied descriptor still lists it as assignable</b> (plan D5): a name
    /// dropped from <c>assignableRoles</c>, a project role and an unknown name are never effective.
    /// </summary>
    [Fact]
    public void Effective_roles_are_the_stored_names_still_assignable()
    {
        var policy = Compile("""{ "createTenants": {}, "assignableRoles": ["dispatcher", "technician"] }""").SelfService;

        Names(policy.Effective(["technician", "clerk", "admin", "ghost", "Dispatcher"])).ShouldBe(["technician"]);
        policy.Effective([]).ShouldBeEmpty();
    }

    [Fact]
    public void Without_self_service_the_policy_is_none()
    {
        var policy = Compile(null).SelfService;

        policy.ShouldBeSameAs(SelfServicePolicy.None);
        policy.Enabled.ShouldBeFalse();
        policy.AssignableRoles.ShouldBeEmpty();
        policy.HasPersonalTenant.ShouldBeFalse();
        policy.CreatesTenants.ShouldBeFalse();
        policy.Effective(["dispatcher"]).ShouldBeEmpty();
    }

    /// <summary>
    /// <b>Default-deny on a path that skipped the apply refusals</b>: a built-in or undeclared assignable name is never a
    /// tenant role, and a personal or creator role outside <c>assignableRoles</c> is dropped rather than granted.
    /// </summary>
    [Fact]
    public void A_descriptor_that_skipped_the_refusals_grants_no_role_outside_the_tenant_roles()
    {
        var descriptor = AlvoDescriptor.Parse("""
            {
              "apiVersion": "alvo.dev/v1", "name": "p", "auth": { "roles": ["dispatcher", "clerk"] },
              "tenancy": { "enabled": true, "selfService": {
                "personalTenant": { "roles": ["clerk", "admin"] },
                "createTenants": { "creatorRoles": ["dispatcher", "admin"] },
                "assignableRoles": ["dispatcher", "admin", "ghost"] } },
              "entities": {}
            }
            """);

        var policy = SelfServicePolicy.From(descriptor, RoleCatalog.FromDescriptor(descriptor));

        Names(policy.AssignableRoles).ShouldBe(["dispatcher"]);
        Names(policy.PersonalTenantRoles).ShouldBeEmpty();
        Names(policy.CreatorRoles).ShouldBe(["dispatcher"]);
    }
}
