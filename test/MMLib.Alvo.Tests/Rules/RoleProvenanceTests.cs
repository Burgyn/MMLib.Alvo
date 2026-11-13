using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// Which role set <c>@user.roles</c> is, by the tenancy of the entity whose scope reads it (spec §6.2): project roles
/// plus tenant roles in a scoped entity's scope, project roles alone everywhere else — and the very set the caller
/// carries, unallocated, whenever the tenant roles cannot count.
/// </summary>
public sealed class RoleProvenanceTests
{
    /// <summary>The declared application role <c>dispatcher</c>, as a one-role set.</summary>
    internal static IReadOnlySet<Role> Dispatcher { get; } = RoleCatalog.Create(["dispatcher"]).Resolve(["dispatcher"]);

    /// <summary>An authenticated Acme caller holding <c>dispatcher</c> as a tenant role only.</summary>
    internal static AlvoContext Person { get; } =
        CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated }, TenantRoles = Dispatcher };

    /// <summary>An authenticated Acme caller with no tenant roles.</summary>
    internal static AlvoContext Plain { get; } = CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated } };

    /// <summary>An authenticated Acme caller holding <c>dispatcher</c> as a project role.</summary>
    internal static AlvoContext ProjectDispatcher { get; } =
        Plain with { Roles = new HashSet<Role>(Dispatcher) { Role.Authenticated } };

    [Fact]
    public void A_scoped_entity_sees_project_and_tenant_roles()
    {
        var visible = RoleProvenance.Visible(Person, TenancyMode.Scoped);

        visible.SetEquals([Role.Authenticated, Dispatcher.Single()]).ShouldBeTrue();
    }

    [Theory]
    [InlineData(TenancyMode.Global)]
    [InlineData(null)]
    public void A_global_or_untenanted_scope_sees_project_roles_only(TenancyMode? tenancy)
    {
        RoleProvenance.Visible(Person, tenancy).ShouldBeSameAs(Person.Roles);
    }

    [Fact]
    public void Tenant_roles_without_a_tenant_count_nowhere()
    {
        var tenantless = Person with { Tenant = null };

        RoleProvenance.Visible(tenantless, TenancyMode.Scoped).ShouldBeSameAs(tenantless.Roles);
    }

    [Fact]
    public void A_caller_without_tenant_roles_allocates_nothing()
    {
        RoleProvenance.Visible(Plain, TenancyMode.Scoped).ShouldBeSameAs(Plain.Roles);
    }
}
